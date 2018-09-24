﻿using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;

namespace Rubeus
{
    public class Networking
    {
        public static string GetDCName()
        {
            // retrieves the current domain controller name

            // adapted from https://www.pinvoke.net/default.aspx/netapi32.dsgetdcname
            Interop.DOMAIN_CONTROLLER_INFO domainInfo;
            const int ERROR_SUCCESS = 0;
            IntPtr pDCI = IntPtr.Zero;

            int val = Interop.DsGetDcName("", "", 0, "",
                Interop.DSGETDCNAME_FLAGS.DS_DIRECTORY_SERVICE_REQUIRED |
                Interop.DSGETDCNAME_FLAGS.DS_RETURN_DNS_NAME |
                Interop.DSGETDCNAME_FLAGS.DS_IP_REQUIRED, out pDCI);

            if (ERROR_SUCCESS == val)
            {
                domainInfo = (Interop.DOMAIN_CONTROLLER_INFO)Marshal.PtrToStructure(pDCI, typeof(Interop.DOMAIN_CONTROLLER_INFO));
                string dcName = domainInfo.DomainControllerName;
                Interop.NetApiBufferFree(pDCI);
                return dcName.Trim('\\');
            }
            else
            {
                string errorMessage = new Win32Exception((int)val).Message;
                Console.WriteLine("\r\n  [X] Error {0} retrieving domain controller : {1}", val, errorMessage);
                Interop.NetApiBufferFree(pDCI);
                return "";
            }
        }

        public static byte[] SendBytes(string server, int port, byte[] data)
        {
            // send the byte array to the specified server/port

            // TODO: try/catch for IPAddress parse

            Console.WriteLine("[*] Connecting to {0}:{1}", server, port);
            System.Net.IPEndPoint endPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(server), port);

            System.Net.Sockets.Socket socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
            socket.Ttl = 128;

            byte[] lenBytes = BitConverter.GetBytes(data.Length);
            Array.Reverse(lenBytes);

            // build byte[req len + req bytes]
            byte[] totalRequestBytes = new byte[lenBytes.Length + data.Length];
            Array.Copy(lenBytes, totalRequestBytes, lenBytes.Length);
            Array.Copy(data, 0, totalRequestBytes, lenBytes.Length, data.Length);

            try
            {
                // connect to the srever over The specified port
                socket.Connect(endPoint);
            }
            catch (Exception e)
            {
                Console.WriteLine("[X] Error connecing to {0}:{1} : {2}", server, port, e.Message);
                return null;
            }

            // actually send the bytes
            int bytesSent = socket.Send(totalRequestBytes);
            Console.WriteLine("[*] Sent {0} bytes", bytesSent);

            byte[] responseBuffer = new byte[2500];
            int bytesReceived = socket.Receive(responseBuffer);
            Console.WriteLine("[*] Received {0} bytes", bytesReceived);

            byte[] response = new byte[bytesReceived - 4];
            Array.Copy(responseBuffer, 4, response, 0, bytesReceived - 4);

            socket.Close();

            return response;
        }
    }
}