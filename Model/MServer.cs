﻿using MIMSS.Model;
using MyMVVM;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SocketAsyncEventArgsOfficeDemo
{
    class MServer: NotifyObject
    {
        private int m_numConnections;       //最大连接数
        private int m_receiveBufferSize;    //每个套接字I/O操作的缓存区大小
        BufferManager m_bufferManager;      //表示所有套接字操作的可重用缓存区集
        const int opsToPreEAlloc = 2;        //读、写(与accepts无关)
        Socket listenSocket;                //监听Socket
        //可重用的SocketAsyncEventArgs对象池，用于写入、读取和接收套接字操作
        SocketAsyncEventArgsPool m_readWritePool;
        int m_totalBytesRead;               //服务器接收到的总字符数
        int m_numConnectedSockets;          //连接到服务器的客户端总数
        Semaphore m_maxNumberAcceptedClients;       //多线程信号量
        public Dictionary<string, SocketAsyncEventArgs> m_sendSaeaDic;   //保存发送SAEA
        public List<AsyncUserToken> m_clientList; //客户端列表  
        public DataBaseQuery dataBaseQuery;
        public Thread scanThread;      //这个线程用来扫描已连接客户端，并发送消息，程序结束时要干掉
        public bool isStart = false;       //指示程序状态

        private static MServer mServer = null;
        public static MServer CreateInstance(int numConnections, int receiveBufferSize)
        {
            if (mServer == null)
            {
                Console.WriteLine("被创建");
                mServer = new MServer(numConnections, receiveBufferSize);
            }
            return mServer;
        }

        public static MServer CreateInstance()
        {
            return mServer;
        }

        private StringBuilder logStringBuild;
        public StringBuilder LogStringBuild
        {
            get { return logStringBuild; }
            set
            {
                logStringBuild = value;
            }
        }

        private String logString;
        public String LogString
        {
            get { return logString; }
            set
            {
                    logString = value;
                    RaisePropertyChanged("LogString");
            }
        }

        //创建一个未初始化的服务器实例
        //开始监听
        //先调用Init方法，然后调用Start方法
        //
        //<param name = "numConnections">同时处理的最大连接数</param>
        //<param name = "receiveBufferSize">用于每个套接字操作的缓存区大小</param>
        private MServer(int numConnections, int receiveBufferSize)
        {
            m_totalBytesRead = 0;               
            m_numConnectedSockets = 0;                  //已连接的客户端数量
            m_numConnections = numConnections;          //可以连接的客户端最大数量
            m_receiveBufferSize = receiveBufferSize;    //每个套接字I/O操作的缓存区大小

            //初始化BufferManeger对象，设置缓存区总的大小，这里还没有真正的分配
            //大小为：最大连接数量客户端  每个有读、写两块区域，大小都为receiveBufferSize
            m_bufferManager = new BufferManager(receiveBufferSize * numConnections * opsToPreEAlloc,
                                                receiveBufferSize);
            m_readWritePool = new SocketAsyncEventArgsPool(numConnections);     //初始化了SAEA对象池，最大容量为总连接数量的两倍，然而这里是一倍
            m_maxNumberAcceptedClients = new Semaphore(numConnections, numConnections);     //初始化信号量，param1为剩余数量，param2为总数量
            m_sendSaeaDic = new Dictionary<string, SocketAsyncEventArgs>();         //初始化sendSAEA字典
            m_clientList = new List<AsyncUserToken>();      //初始化客户端列表

            //初始化日志str
            logStringBuild = new StringBuilder();
            //初始化数据库连接
            dataBaseQuery = new DataBaseQuery("mylinedatabase", "127.0.0.1", "root", "lmbdyn1997725", "None");
        }

        //通过预分配可重用缓存区和上下文对象来初始化服务器。这些对象不需要
        //预先分配或重用，这样做使为了说明如何轻松地使用API创建可重用对象以
        //提高服务器性能
        public void Init()
        {
            m_clientList.Clear();
            //这里才真正向系统申请缓存区
            m_bufferManager.InitBuffer();

            //声明一个SAEA对象，用于后面的操作
            SocketAsyncEventArgs readWriteEventArg;

            //new最大连接数量个SAEA对象，依次绑定好事件(或者委托?)，分配用户信息存储空间，分配好空间，然后放入SAEA池
            for (int i = 0; i < m_numConnections; i++)
            {
                readWriteEventArg = new SocketAsyncEventArgs();
                //绑定完成事件
                readWriteEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(IO_Completed);
                //分配用户信息空间，UserToken是object类型的对象，而右边的new AsyncUserToken()是自定义的用户信息类
                readWriteEventArg.UserToken = new AsyncUserToken();

                //将缓冲池中的字节缓冲区分配给SAEA对象
                m_bufferManager.SetBuffer(readWriteEventArg);

                //将SAEA放入SAEA池中
                m_readWritePool.Push(readWriteEventArg);
            }
        }

        //启动服务，监听连接请求
        //
        //<param name = "localEndPoint"> 服务端监听的目标端口 </param>
        public void Start(IPEndPoint localEndPoint)
        {
            //创建监听用socket
            listenSocket = new Socket(localEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            listenSocket.Bind(localEndPoint);
            //开始监听，最大同时监听数为100；
            listenSocket.Listen(100);

            //开始接收连接
            StartAccept(null);

            LogStringBuild.Append("开始监听\n");
            LogString = LogStringBuild.ToString();

            //新开一个线程用作扫描新消息
            scanThread = new Thread(ScanFriendTable);
            scanThread.Start();
            //Console.WriteLine("按任意键终止服务器进程...");
            //Console.ReadKey();          //获取用户按下的下一个字符或者功能键
            isStart = true;
        }

        public void Stop()
        {
            foreach (AsyncUserToken token in m_clientList)
            {
                try
                {
                    token.Socket.Shutdown(SocketShutdown.Both);
                }
                catch (Exception) { }
            }
            try
            {
                listenSocket.Shutdown(SocketShutdown.Both);
            }
            catch (Exception) { }

            listenSocket.Close();
            int c_count = m_clientList.Count;
            lock (m_clientList) { m_clientList.Clear(); }
            //直接退出程序算了
            System.Environment.Exit(0);

            //if (ClientNumberChange != null)
            //  ClientNumberChange(-c_count, null);
        }

        //开始接收服务器请求
        //
        //<param nmame = "acceptEvent">在服务器的监听套接字上发出accept操作时使用的上下文对象</param>
        public void StartAccept(SocketAsyncEventArgs acceptEventArg)
        {
            //这里应该是使用一个新的SAEA用于接收连接
            if (acceptEventArg == null)
            {
                acceptEventArg = new SocketAsyncEventArgs();
                acceptEventArg.Completed += new EventHandler<SocketAsyncEventArgs>(AcceptEventArg_Completed);              
            }
            else
            {
                //因为正在重用上下文对象，所以必须清除套接字
                acceptEventArg.AcceptSocket = null;           
            }


            //申请一个信号量
            m_maxNumberAcceptedClients.WaitOne();
            //将acceptEventArg用于接收连接，如果IO挂起，则返回true
            Console.WriteLine("开始等待连接");
            bool willRaiseEvent = listenSocket.AcceptAsync(acceptEventArg);
            if (!willRaiseEvent)
            {
                ProcessAccept(acceptEventArg);
            }

        }

        //此方法是和Acceptsync关联的回调函数，在接收完成时调用
        void AcceptEventArg_Completed(object sender, SocketAsyncEventArgs e)
        {
            ProcessAccept(e);
        }
    

        //starty()与ProcessAccept()构成一个接受连接的循环
        private void ProcessAccept(SocketAsyncEventArgs e)
        {
            //TODO  程序退出时会收到一个连接，原因不明
            try
            {
                // 原子操作，使m_numConnectedSockets增加1
                Interlocked.Increment(ref m_numConnectedSockets);
                //Console.WriteLine("Client connection accepted.There are {0} clients connected to the server", m_numConnectedSockets)
                LogStringBuild.AppendFormat("Client connection accepted.There are {0} clients connected to the server\n", m_numConnectedSockets);
                LogString = LogStringBuild.ToString();

                //弹出一个SAEA池中的SAEA,这个SAEA是用来接收消息的
                SocketAsyncEventArgs readEventArgs = m_readWritePool.Pop();

                //将usertoken放入客户端列表
                lock (m_clientList) { m_clientList.Add((AsyncUserToken)readEventArgs.UserToken); }

            //获取已接受的客户端连接的套接字和IP端口放入用户信息中(receiveSaea)
            ((AsyncUserToken)readEventArgs.UserToken).Socket = e.AcceptSocket;
                ((AsyncUserToken)readEventArgs.UserToken).Remote = e.RemoteEndPoint;

                //弹出一个SAEA，这个SAEA是用来发送消息的
                //TODO  弹出来而不是new一个   
                SocketAsyncEventArgs sendSaea = new SocketAsyncEventArgs();
                //将发送SAEA放入sendSaeaDic字典，方便以后使用
                m_sendSaeaDic.Add(e.AcceptSocket.RemoteEndPoint.ToString(), sendSaea);
                //分配缓存区
                //m_bufferManager.SetBuffer(sendSaea);

                //将sendSaea指向readEentArgs的usertoken,这样，发送与接收的usertoken都是一个了
                sendSaea.UserToken = (AsyncUserToken)readEventArgs.UserToken;

                Console.WriteLine("The Client IP:" + e.RemoteEndPoint);

                //e.AcceptSocket代表已接收到的客户端连接的socket
                //将readEventArgs用于接收消息
                //IO挂起后，返回true，同时引发参数的Completed事件   如果IO操作同步完成，返回false，并且不会引发Completed事件
                //这里的ReceiveAsync是必须的，是为了进入读取->接收->读取的循环。当然，在后面也可以自己写成读取->读取的内循环
                bool willRaiseEvent = e.AcceptSocket.ReceiveAsync(readEventArgs);
                if (!willRaiseEvent)
                {
                    ProcessReceive(readEventArgs);
                }

                //接收下一个连接
                StartAccept(e);
            }
            catch (Exception ex)
            {
                Console.WriteLine("ProcessAccept Error : " + ex);
            }
            
        }

        //当一个接收或者发送操作完成时调用此函数
        //
        //<param name = "e"> SocketAsyncEventArg </param>
        void IO_Completed(object sender, SocketAsyncEventArgs e)
        {
            //确定刚刚完成的操作类型并调用关联的处理程序
            switch (e.LastOperation)
            {
                case SocketAsyncOperation.Receive:
                    Console.WriteLine(sender.ToString() + "调用了一次回调函数 RECV");
                    ProcessReceive(e);
                    break;
                case SocketAsyncOperation.Send:
                    Console.WriteLine(sender.ToString() + "调用了一次回调函数 SEND");
                    ProcessSend(e);
                    break;
                default:
                    throw new ArgumentException("Last Operation error");
            }
        }

        //异步接收操作完成时调用此方法
        //如果远程主机关闭了连接，那么就关闭套接字
        //接收到了数据，将数据返回给客户端
        //服务端的没用重复调用ProcessReceive函数，因为前面根本没有绑定。。
        private void ProcessReceive(SocketAsyncEventArgs e)
        {
            AsyncUserToken token = (AsyncUserToken)e.UserToken;
            try
            {
                //检查这个远程主机是否关闭连接
               
                if (e.BytesTransferred > 0 && e.SocketError == SocketError.Success)
                {
                    //增加服务器接收到的总字数
                    Interlocked.Add(ref m_totalBytesRead, e.BytesTransferred);
                    LogStringBuild.AppendFormat("The server has read a total of {0} bytes\n", m_totalBytesRead);
                    LogString = LogStringBuild.ToString();

                    //使用MessageDeal类处理数据
                    do
                    {
                        MessageDeal.ReceiveDeal(e);
                    }
                    while (token.receiveBuffer.Count > 8 && token.packageLen == 0);

                    //这里每一次接收到数据后，就会调用发送函数的回调函数
                    //那么后面服务端自己主动发送的时候，就需要自己主动调用了
                    //if (token.sendPacketNum.Count() > 0)
                    //{
                    //    //调用发送函数的回调函数
                    //    ProcessSend(m_sendSaeaDic[token.Socket.RemoteEndPoint.ToString()]);
                    //}

                    //将数据返回给客户端
                    //e.SetBuffer(e.Offset, e.BytesTransferred);
                    //这里是从读取到发送
                    Console.WriteLine("开始异步接收");
                    lock (e)
                    {
                        token.isCopy = false;
                        bool willRaiseEvent = token.Socket.ReceiveAsync(e);
                        if (!willRaiseEvent)
                        {
                            ProcessReceive(e);
                        }
                    }      
                }
                else
                {
                    CloseClientSocket(e);
                }
            }
            catch (Exception xe)
            {
                Console.WriteLine(xe.Message + "\r\n" + xe.StackTrace);
            }
        }

        public void SendMessage(int packageType, String str, SocketAsyncEventArgs e)
        {
            AsyncUserToken token = (AsyncUserToken)e.UserToken;
            //这里是要获得字节数而不是元素数
            int packageLen = System.Text.Encoding.Default.GetByteCount(str) + 8;
            Console.WriteLine("包的大小为" + packageLen);
            byte[] bType = System.BitConverter.GetBytes(packageType);
            byte[] bLen = System.BitConverter.GetBytes(packageLen);
            //将数据放入发送buffer
            token.sendBuffer.AddRange(bType);
            token.sendBuffer.AddRange(bLen);
            token.sendBuffer.AddRange(System.Text.Encoding.Default.GetBytes(str));
            //接下来可以调用发送函数的回调函数了
            //下一次要发送多少数据
            token.sendPacketNum.Add(packageLen);
            //调用发送函数
            ProcessSend(e);
        }


        //TODO 因为异步发送会遇到SAEA占用问题，所以改成了同步发送，那么sendSAEA是不是可以去掉？
        //异步发送操作完成时调用此方法
        //该方法在套接字上发出另一个接收以读取任何其他接收  ??
        //从客户端发送的数据
        //
        //<param name = "e"></param>
        private void ProcessSend(SocketAsyncEventArgs e)
        {
            try
            {
                if (((AsyncUserToken)e.UserToken).sendBuffer.Count == 0)
                {
                    Console.WriteLine("没有剩余数据，退出");
                    return;
                }

                //将SAEA变成发送的
                e = m_sendSaeaDic[((AsyncUserToken)e.UserToken).Socket.RemoteEndPoint.ToString()];

                Console.WriteLine("LastOperation = " + e.LastOperation);

                if (e.SocketError == SocketError.Success)
                {
                    //完成了将数据返回给客户端
                    AsyncUserToken token = (AsyncUserToken)e.UserToken;
                    byte[] data;
                    int count = token.sendBuffer.Count;

                    //if ( count > 1024)
                    //{
                    //    data = token.sendBuffer.GetRange(0, 1024).ToArray();
                    //    token.sendBuffer.RemoveRange(0, 1024);
                    //}
                    //else
                    //{
                    //    data = token.sendBuffer.GetRange(0, count).ToArray();
                    //    token.sendBuffer.RemoveRange(0, count);
                    //}

                    data = token.sendBuffer.ToArray();
                    token.sendBuffer.Clear();

                    //e.SetBuffer(data, 0, data.Length);

                    Console.WriteLine("开始同步发送 datasize:" + data.Count() + "data:" + System.Text.Encoding.UTF8.GetString(data));
                    token.Socket.Send(data);


                    //Console.WriteLine("开始异步发送 datasize:" + data.Count() + "data:" + System.Text.Encoding.UTF8.GetString(data));

                    //bool willRaiseEvent = token.Socket.SendAsync(e);
                    //if (!willRaiseEvent)
                    //{
                    //    Console.WriteLine("调用了这个吗?");
                    //    ProcessSend(e);
                    //}

                    Console.WriteLine("同步发送完毕");
                }
                else
                {
                    CloseClientSocket(e);
                }
            }
            catch (Exception ex)
            {
                CloseClientSocket(e); ;
            }
                 
        }

        private void CloseClientSocket(SocketAsyncEventArgs e)
        {
            Console.WriteLine("释放连接资源");
            AsyncUserToken token = e.UserToken as AsyncUserToken;
            string dicStr = token.Socket.RemoteEndPoint.ToString();

            //关闭与客户端关联的套接字
            try
            {
                token.Socket.Shutdown(SocketShutdown.Send);
            }
            //throws if client process has already closed
            catch (Exception)
            { token.Socket.Close(); }

            //原子操作，使m_numConnectedSockets减一
            Interlocked.Decrement(ref m_numConnectedSockets);

            
            //先释放分配的缓存区，然后从字典中删除sendSaea对象
            //m_bufferManager.FreeBuffer(m_sendSaeaDic[dicStr]);
            m_sendSaeaDic.Remove(dicStr);
            Console.WriteLine("sendSaeaDic Count = " + m_sendSaeaDic.Count());
            //释放接收SocktAsyncEventArg，然后可被重用到其他客户端(这里直接放入就行了)
            m_readWritePool.Push(e);

            //因为断开了连接，所以将状态设为离线，即0
            this.dataBaseQuery.SetStatus(((AsyncUserToken)e.UserToken).UserId, 0);
            ((AsyncUserToken)e.UserToken).UserId = null;
            //从客户端列表中移除
            lock (m_clientList) { m_clientList.Remove((AsyncUserToken)e.UserToken); }

            m_maxNumberAcceptedClients.Release();
            LogStringBuild.AppendFormat("A client has benn disconnected from the server.There are {0} clients connected to the server\n", m_numConnectedSockets);
            LogString = LogStringBuild.ToString();
        }

        //TODO  这个函数要完善
        //每隔3秒循环扫描已连接用户的好友列表和消息表，并将好友消息和消息发送给客户端
        public void ScanFriendTable()
        {
            while (true)
            {
                Thread.Sleep(3000);
                try
                {
                    if (m_clientList.Count > 0)
                    {
                        //当客户端连接时，m_clienList的总数就会加1，但是此时还不知道用户的Id

                        foreach (var mclien in m_clientList)
                        {

                            //id不为空的时候才查询
                            if (mclien.UserId != null)
                            {
                                Console.Write("循环查询" + mclien.UserId + "的一系列消息");
                                //MessageBox.Show(mclien.UserId);
                                //查询该用户的信息表是否有信息，并发送给该用户,消息类型为5
                                String sendStr = mServer.dataBaseQuery.UserMessageQuery(mclien.UserId);
                                //有数据才发
                                if (sendStr.Equals("null") != true)
                                {
                                    mServer.SendMessage(5, sendStr, m_sendSaeaDic[mclien.Socket.RemoteEndPoint.ToString()]);
                                }

                                //查询该用户好友的状态，发送给该用户，消息类型为16
                                sendStr = mServer.dataBaseQuery.UserFriendStatus(mclien.UserId);
                                //有数据才发
                                if (sendStr.Equals("null") != true)
                                {
                                    mServer.SendMessage(16, sendStr, m_sendSaeaDic[mclien.Socket.RemoteEndPoint.ToString()]);
                                }


                                //TODO
                                //新的好友和新的好友请求
                                //改成该用户在线就发送，没有在线就直接保存在表里面
                                //好友状态不应该保存在客户端本地数据库里面，每次更新数据库代价太大，而是动态保存

                                ////查询该用户好友的信息
                                //sendStr = mServer.dataBaseQuery.FriendInfoQuery(mclien.UserId);
                                ////有数据才发
                                //if (sendStr.Equals("null") != true)
                                //{
                                //    mServer.SendMessage(4, sendStr, m_sendSaeaDic[mclien.Socket.RemoteEndPoint.ToString()]);
                                //}

                                //////查询该用户好友请求表
                                //sendStr = mServer.dataBaseQuery.FriendRequestQuery(mclien.UserId);
                                ////有数据才发
                                //if (sendStr.Equals("null") != true)
                                //{
                                //    mServer.SendMessage(10, sendStr, m_sendSaeaDic[mclien.Socket.RemoteEndPoint.ToString()]);
                                //}
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ScanFriendTable Error : " + ex);
                }
                   
            }
        }

    }
}
