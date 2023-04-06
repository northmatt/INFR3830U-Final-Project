﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Net.Sockets;
using System.Net;

namespace Server {
    struct ClientInfo {
        public byte id;
        public Socket TCPSocket;
        public EndPoint UDPEndpoint;
        public float[] position;
    }

    [Flags] enum DirtyFlag {
        None = 0,
        Position = 1,
        Message = 2
    }

    public enum ServerNetworkCalls : byte {
        TCPClientMessage = 0,
        UDPClientConnection = 0 + 0x10,
        UDPClientTransform = 1 + 0x10
    }

    public enum ClientNetworkCalls : byte {
        TCPClientConnection = 0,
        TCPClientDisconnection = 1,
        TCPClientTransform = 2,
        TCPClientsTransform = 3,
        TCPClientMessage = 4,
        UDPClientsTransform = 0 + 0x10
    }

    class Program {
        private static byte[] receiveBuffer = new byte[1024];
        private static Socket TCPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private static Socket UDPSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        private static EndPoint UDPReceiveEndPoint;
        private static ClientInfo tempClientInfo;
        private static List<ClientInfo> clientInfoList = new List<ClientInfo>();
        private static bool shutdownServer = false;
        private static DirtyFlag dirtyFlag = DirtyFlag.None;

        static void Main(string[] args) {
            StartServer("127.0.0.1", 8888);

            Thread sendThread = new Thread(new ThreadStart(SendLoop));
            sendThread.Name = "SendThread";
            sendThread.Start();

            Console.ReadLine();

            ShutdownServer();

            Console.ReadLine();
        }

        public static void StartServer(string address, int port) {
            Console.WriteLine("Press Return to close server.");
            Console.WriteLine("Starting Server...");
            TCPSocket.Bind(new IPEndPoint(IPAddress.Parse(address), port));

            UDPReceiveEndPoint = new IPEndPoint(IPAddress.Any, 0);
            UDPSocket.Bind(new IPEndPoint(IPAddress.Parse(address), port + 1));

            TCPSocket.Listen(10);
            //Timeout is 200ms
            UDPSocket.SendTimeout = 200;
            UDPSocket.ReceiveTimeout = 200;
            TCPSocket.SendTimeout = 200;
            TCPSocket.ReceiveTimeout = 200;
            //Buffersize is 2x data needed to be sent
            UDPSocket.SendBufferSize = 1024;
            UDPSocket.ReceiveBufferSize = 1024;
            TCPSocket.SendBufferSize = 1024;
            TCPSocket.ReceiveBufferSize = 1024;

            tempClientInfo.TCPSocket = null;
            tempClientInfo.UDPEndpoint = null;

            TCPSocket.BeginAccept(new AsyncCallback(AcceptCallback), null);
            UDPSocket.BeginReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, 0, ref UDPReceiveEndPoint, new AsyncCallback(ReceiveUDPCallback), UDPSocket);
        }

        public static void ShutdownServer() {
            //Release server socket resources
            try {
                shutdownServer = true;

                foreach (ClientInfo client in clientInfoList) {
                    DisconnectClient(client);
                }

                TCPSocket.Close();
                UDPSocket.Close();

                Console.WriteLine("Stopped Server");

                return;
            }
            catch (System.Exception exc) {
                Console.WriteLine("Exception: " + exc.ToString());
            }

            //Release server socket resources
            TCPSocket.Close();
            UDPSocket.Close();
            Console.WriteLine("Stopped Server");

            return;
        }

        public static void DisconnectClient(ClientInfo client, bool removeFromList = false) {
            try {
                string clientIp = client.TCPSocket.RemoteEndPoint.ToString();
                //Console.WriteLine("Disconnecting Client " + clientIp);

                client.TCPSocket.Shutdown(SocketShutdown.Both);
                client.TCPSocket.Close();

                if (removeFromList)
                    clientInfoList.Remove(client);

                Console.WriteLine("Disconnected Client {0} (TCP {1})", client.id, clientIp);
            }
            catch (System.Exception exc) {
                Console.WriteLine("Exception: " + exc.ToString());
            }
            
        }

        public static void DisconnectClient(Socket tcpSocket) {
            try {
                string clientIp = tcpSocket.RemoteEndPoint.ToString();
                //Console.WriteLine("Disconnecting Client " + clientIp);

                ClientInfo client = FindClient(tcpSocket);

                if (client.TCPSocket == null || client.UDPEndpoint == null) {
                    Console.WriteLine("Cant disconnect client, client {0} not found", clientIp);
                    return;
                }

                DisconnectClient(client, true);

                //Tell other clients about disconnection
                byte[] sendBuffer = new byte[4];
                BufferSetup(sendBuffer, client.id, true);
                SendNetworkCallback(clientInfoList, sendBuffer);
            }
            catch (System.Exception exc) {
                Console.WriteLine("Exception: " + exc.ToString());
            }
        }

        private static void AcceptCallback(IAsyncResult result) {
            if (shutdownServer)
                return;

            try {
                Socket clientTCPSocket = TCPSocket.EndAccept(result);

                IPEndPoint TCPEndPoint = (IPEndPoint)clientTCPSocket.RemoteEndPoint;
                //Console.WriteLine("TCP Client {0}:{1} connected", TCPEndPoint.Address.ToString(), TCPEndPoint.Port.ToString());
                //Timeout is 200ms
                clientTCPSocket.SendTimeout = 200;
                clientTCPSocket.ReceiveTimeout = 200;
                //Buffersize is 2x data needed to be sent
                clientTCPSocket.SendBufferSize = 1024;
                clientTCPSocket.ReceiveBufferSize = 1024;

                tempClientInfo.TCPSocket = clientTCPSocket;

                Thread clientInfoThread = new Thread(new ThreadStart(SetupClientInfo));
                clientInfoThread.Name = "serverClientInfoThread";
                clientInfoThread.Start();
            }
            catch (System.Exception exc) {
                Console.WriteLine("Exception: " + exc.ToString());
            }
        }

        private static void ReceiveTCPCallback(IAsyncResult result) {
            if (shutdownServer)
                return;

            Socket socket = (Socket)result.AsyncState;
            int bufferLength = 0;

            try {
                bufferLength = socket.EndReceive(result);
            }
            catch (SocketException exc) {
                Console.WriteLine("Socket Exception: " + exc.ToString());
            }
            catch (Exception exc) {
                Console.WriteLine("Exception: " + exc.ToString());
            }

            //Handle Disconnect
            if (bufferLength == 0) {
                DisconnectClient(socket);
                return;
            }

            byte[] data = new byte[bufferLength];
            Array.Copy(receiveBuffer, data, bufferLength);
            socket.BeginReceive(receiveBuffer, 0, receiveBuffer.Length, 0, new AsyncCallback(ReceiveTCPCallback), socket);

            //First byte is the "protocol"/"type" of data sent
            switch ((ServerNetworkCalls)data[0]) {
                case ServerNetworkCalls.TCPClientMessage:
                    string message = Encoding.ASCII.GetString(data, 4, bufferLength - 4);
                    message = "C" + data[1] + ": " + message;
                    Console.WriteLine(message);

                    data[0] = (byte)ClientNetworkCalls.TCPClientMessage;
                    SendNetworkCallback(clientInfoList, data);
                    break;
                default:
                    break;
            }
        }

        private static void ReceiveUDPCallback(IAsyncResult result) {
            if (shutdownServer)
                return;

            Socket socket = (Socket)result.AsyncState;
            int bufferLength = 0;

            try {
                bufferLength = socket.EndReceiveFrom(result, ref UDPReceiveEndPoint);
            }
            catch (System.Exception exc) {
                Console.WriteLine("Exception: " + exc.ToString());
                socket.BeginReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, 0, ref UDPReceiveEndPoint, new AsyncCallback(ReceiveUDPCallback), socket);
                return;
            }

            byte[] data = new byte[bufferLength];
            Array.Copy(receiveBuffer, data, bufferLength);
            socket.BeginReceiveFrom(receiveBuffer, 0, receiveBuffer.Length, 0, ref UDPReceiveEndPoint, new AsyncCallback(ReceiveUDPCallback), socket);

            //First byte is the "protocol"/"type" of data sent
            switch ((ServerNetworkCalls)data[0]) {
                case ServerNetworkCalls.UDPClientConnection:
                    if (bufferLength != 4)
                        break;

                    if (tempClientInfo.TCPSocket == null || tempClientInfo.UDPEndpoint != null || data[1] != tempClientInfo.id)
                        break;

                    IPEndPoint UDPEP = (IPEndPoint)UDPReceiveEndPoint;
                    tempClientInfo.UDPEndpoint = UDPEP;
                    //Console.WriteLine("UDP Client {0}:{1} connected", test.Address, test.Port);

                    break;
                case ServerNetworkCalls.UDPClientTransform:
                    if (bufferLength != 16)
                        break;

                    ClientInfo client = FindClient(data[1]);
                    if (client.TCPSocket == null)
                        break;

                    client.position[0] = BitConverter.ToSingle(data, 4);
                    client.position[1] = BitConverter.ToSingle(data, 8);
                    client.position[2] = BitConverter.ToSingle(data, 12);

                    dirtyFlag = DirtyFlag.Position;
                    //Console.WriteLine("Client {0} with UDP IP {1}:{2} has new pos of {3}, {4}, {5}", client.id, UDPEP.Address, UDPEP.Port, client.position[0], client.position[1], client.position[2]);
                    break;
                default:
                    break;
            }
        }

        private static void SendNetworkCallback(ClientInfo client, byte[] sendBuffer) {
            if ((sendBuffer[0] & 0x10) == 0x10)
                UDPSocket.BeginSendTo(sendBuffer, 0, sendBuffer.Length, 0, client.UDPEndpoint, new AsyncCallback(SendUDPCallback), UDPSocket); 
            else
                client.TCPSocket.BeginSend(sendBuffer, 0, sendBuffer.Length, 0, new AsyncCallback(SendTCPCallback), client.TCPSocket);
        }

        private static void SendNetworkCallback(List<ClientInfo> clients, byte[] sendBuffer) {
            foreach (ClientInfo client in clients) {
                SendNetworkCallback(client, sendBuffer);
            }
        }

        private static void SendTCPCallback(IAsyncResult result) {
            if (shutdownServer)
                return;

            Socket socket = (Socket)result.AsyncState;
            socket.EndSend(result);
        }

        private static void SendUDPCallback(IAsyncResult result) {
            if (shutdownServer)
                return;

            Socket socket = (Socket)result.AsyncState;
            socket.EndSendTo(result);
        }

        private static void SetupClientInfo() {
            //Establish a TCP and UDP connection to get TCP/UDP ports of a client
            byte[] sendBuffer = new byte[4];
            BufferSetup(sendBuffer, FindLowestAvailableClientID(), false);
            SendNetworkCallback(tempClientInfo, sendBuffer);

            tempClientInfo.id = sendBuffer[1];

            //Wait for UDP packet
            byte counter = 0;
            while (tempClientInfo.TCPSocket != null && tempClientInfo.UDPEndpoint != null || counter > 50) {
                Thread.Sleep(100);
                counter++;
            }

            Thread.Sleep(100);

            if (tempClientInfo.TCPSocket != null && tempClientInfo.UDPEndpoint != null) {
                IPEndPoint TCPEP = (IPEndPoint)tempClientInfo.TCPSocket.RemoteEndPoint;
                IPEndPoint UDPEP = (IPEndPoint)tempClientInfo.UDPEndpoint;
                Console.WriteLine("Client {0} on IP {1} with TCP port {2} and UDP port {3} has connected", tempClientInfo.id, TCPEP.Address, TCPEP.Port, UDPEP.Port);

                tempClientInfo.position = new float[] { 0f, 0f, 0f };

                //Tell new client what other clients exist on server
                sendBuffer = new byte[8 + clientInfoList.Count * 16];
                BufferSetup(sendBuffer, clientInfoList, true);
                SendNetworkCallback(tempClientInfo, sendBuffer);

                //Tell other clients new client joined
                sendBuffer = new byte[16];
                BufferSetup(sendBuffer, tempClientInfo);
                SendNetworkCallback(clientInfoList, sendBuffer);

                tempClientInfo.TCPSocket.BeginReceive(receiveBuffer, 0, receiveBuffer.Length, 0, new AsyncCallback(ReceiveTCPCallback), tempClientInfo.TCPSocket);
                clientInfoList.Add(tempClientInfo);
            }
            else if (tempClientInfo.TCPSocket != null) {
                IPEndPoint TCPEP = (IPEndPoint)tempClientInfo.TCPSocket.RemoteEndPoint;
                Console.WriteLine("Client {0} with TCP IP {1}:{2} failed to make a UDP connection", tempClientInfo.id, TCPEP.Address, TCPEP.Port);
                DisconnectClient(tempClientInfo);
            }
            else {
                Console.WriteLine("Client failed to connect");
            }


            tempClientInfo.id = 0;
            tempClientInfo.TCPSocket = null;
            tempClientInfo.UDPEndpoint = null;
            tempClientInfo.position = null;

            TCPSocket.BeginAccept(new AsyncCallback(AcceptCallback), null);
        }
       
        private static void SendLoop() {
            long currentTime = 0;
            long previousTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
            long timeWait = 1000 / 20 - 4; //Windows scheduler cant really wait that accurately
            long deltaTime = 0;

            while (!shutdownServer) {
                currentTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                //Find the amount of time that has passed since this function was last called
                long workTime = currentTime - previousTime;

                //If the amount of time that has passed is smaller than the time to wait it'll find out how long it needs to wait and stop the program from running for a certain amoount of time
                if (workTime < timeWait)
                    Thread.Sleep((int)(timeWait - workTime));

                //Gets deltaTime by looking at difference of current time and previous time
                currentTime = DateTime.Now.Ticks / TimeSpan.TicksPerMillisecond;
                deltaTime = currentTime - previousTime;
                previousTime = currentTime;

                if ((dirtyFlag & DirtyFlag.Position) == 0)
                    continue;

                dirtyFlag = DirtyFlag.None;
                byte[] sendBuffer = new byte[8 + clientInfoList.Count * 16];
                BufferSetup(sendBuffer, clientInfoList, false);

                SendNetworkCallback(clientInfoList, sendBuffer);
            }
        }

        private static byte FindLowestAvailableClientID() {
            byte lowestAvailableID = 0;

            //Start at an ID of 0, stop if "lowestAvailableID" differs from "i"
            //Increase "lowestAvailableID" if any ID matches occur
            //"lowestAvailableID" can only differ from "i" if no matches occur
            for (byte i = lowestAvailableID; i == lowestAvailableID && i < byte.MaxValue; ++i) {
                foreach (ClientInfo client in clientInfoList) {
                    if (client.id == i) {
                        ++lowestAvailableID;
                        break;
                    }
                }
            }

            return lowestAvailableID;
        }

        private static ClientInfo FindClient(Socket tcpSocket) {
            ClientInfo client;
            client.id = 0;
            client.TCPSocket = null;
            client.UDPEndpoint = null;
            client.position = null;

            foreach (ClientInfo curClient in clientInfoList) {
                if (curClient.TCPSocket == tcpSocket) {
                    client = curClient;
                    break;
                }
            }

            return client;
        }

        private static ClientInfo FindClient(byte clientId) {
            ClientInfo client;
            client.id = 0;
            client.TCPSocket = null;
            client.UDPEndpoint = null;
            client.position = null;

            foreach (ClientInfo curClient in clientInfoList) {
                if (curClient.id == clientId) {
                    client = curClient;
                    break;
                }
            }

            return client;
        }

        private static void BufferSetup(byte[] buffer, byte id, bool disconnect) {
            //(dis)connection with ID, TCP
            buffer[0] = disconnect ? (byte)ClientNetworkCalls.TCPClientDisconnection : (byte)ClientNetworkCalls.TCPClientConnection;
            buffer[1] = id;
        }

        private static void BufferSetup(byte[] buffer, List<ClientInfo> clientInfoList, bool useTCP) {
            //Send Pos, TCP/UDP
            buffer[0] = useTCP ? (byte)ClientNetworkCalls.TCPClientsTransform : (byte)ClientNetworkCalls.UDPClientsTransform;
            BitConverter.GetBytes(clientInfoList.Count).CopyTo(buffer, 4);

            int offset = 0;
            for (int index = 0; index < clientInfoList.Count; ++index) {
                offset = index * 16;
                buffer[8 + offset] = clientInfoList[index].id;
                BitConverter.GetBytes(clientInfoList[index].position[0]).CopyTo(buffer, 12 + offset);
                BitConverter.GetBytes(clientInfoList[index].position[1]).CopyTo(buffer, 16 + offset);
                BitConverter.GetBytes(clientInfoList[index].position[2]).CopyTo(buffer, 20 + offset);
            }
        }

        private static void BufferSetup(byte[] buffer, ClientInfo clientInfo) {
            //Send Pos, TCP
            buffer[0] = (byte)ClientNetworkCalls.TCPClientTransform;
            buffer[1] = clientInfo.id;
            BitConverter.GetBytes(clientInfo.position[0]).CopyTo(buffer, 4);
            BitConverter.GetBytes(clientInfo.position[1]).CopyTo(buffer, 8);
            BitConverter.GetBytes(clientInfo.position[2]).CopyTo(buffer, 12);
        }
    }
}