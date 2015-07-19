using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Net;
using System.Net.Mail;
using System.Xml;

namespace eNPCScan
{

    class Program
    {

        private static String strWoWCache;
        private static String strRecipientAddress;
        private static Int32 intScanInterval = 5000; // Default value can be overriden in Settings.xml.
        private static SMTPServer server;
        private static SMTPUser user;
        private static List<NPC> WatchList = new List<NPC>();
        private static List<NPC> DispatchedList = new List<NPC>();

        public enum ConsoleMessage { INFO = 0, FATAL = 1, WARN = 2, FOUND = 3 };

        static void Main(string[] args)
        {

            // Console Appearence
            Console.Title = "External NPCScan";
            Console.CursorVisible = false;

            // Attempt to load the configuration document.
            if (!LoadConfiguration())
            {
                ConsoleLogMessage("Couldn't load the scanner configuration.", ConsoleMessage.FATAL);
            }

            // Check for those already cached and suppress notifications.
            foreach (NPC cNPC in WatchList)
            {
                if (CheckCacheForNPC(cNPC.getID()))
                {
                    ConsoleLogMessage(cNPC.getName() + " (" + cNPC.getID().ToString("D") + ") is already cached. Supressing.", ConsoleMessage.WARN);
                    DispatchedList.Add(cNPC);
                }
            }

            // Continually rescan for NPCs.
            ConsoleLogMessage("Beginning. Scanning every " + intScanInterval.ToString() + "ms.", ConsoleMessage.INFO);
            while (true)
            {
                foreach (NPC cNPC in WatchList)
                {
                    if (!DispatchedNotification(cNPC)
                        && CheckCacheForNPC(cNPC.getID()))
                    {
                        ConsoleLogMessage("NPC " + cNPC.getID().ToString() + ".", ConsoleMessage.FOUND);
                        if (!DispatchedNotification(cNPC))
                        {
                            if (!SendMailNotification(cNPC))
                            {
                                ConsoleLogMessage("Couldn't send the e-mail notification.", ConsoleMessage.WARN);
                            }
                            // Even if it didn't send, continually retrying when
                            // the connection is timing out will block further checking
                            // until the TCP connection times out, so pretend that it was sent.
                            DispatchedList.Add(cNPC);
                        }
                    }
                }

                System.Threading.Thread.Sleep(intScanInterval);

            }

        }

        private static bool LoadConfiguration()
        {

            String strConfigPath = "Settings.xml";

            try
            {

                XmlDocument doc = new XmlDocument();
                doc.Load(strConfigPath);

                XmlNodeList xmlWow = doc.GetElementsByTagName("wow");
                strWoWCache = xmlWow[0].Attributes["cachePath"].Value;

                XmlNodeList xmlNPCs = doc.GetElementsByTagName("npc");
                for (int i = 0; i < xmlNPCs.Count; i++)
                {
                    WatchList.Add(new NPC(xmlNPCs[i]));
                }

                server = new SMTPServer(doc.GetElementsByTagName("smtpServer")[0]);
                user = new SMTPUser(doc.GetElementsByTagName("smtpUser")[0]);

                XmlNode xmlRecipient = doc.GetElementsByTagName("smtpRecipient")[0];
                strRecipientAddress = xmlRecipient.Attributes["address"].Value.ToString();

                intScanInterval = Convert.ToInt32(doc.GetElementsByTagName("scan")[0].Attributes["interval"].Value.ToString(), 10);

            }
            catch (Exception)
            {
                ConsoleLogMessage("General configuration parsing failure.", ConsoleMessage.FATAL);
                return false;
            }

            return true;
        }

        private static bool CheckCacheForNPC(UInt16 intCreatureID)
        {

            // Calculate Reverse ID
            String strHexID = intCreatureID.ToString("X");
            byte[] npc = new byte[2];

            // This is the important part!
            // The two addresses (2 x 8 bits, where each address is 8 bits of 2 Hexadecimal values)
            // are in reverse order, so we reverse them to get the equivalent decimal NPC ID.
            npc[0] = Convert.ToByte(strHexID.Substring(2, 2), 16);
            npc[1] = Convert.ToByte(strHexID.Substring(0, 2), 16);

            // Search for NPC ID in WDB.
            using (FileStream stream = new FileStream(strWoWCache, FileMode.Open))
            {
                using (BinaryReader reader = new BinaryReader(stream))
                {
                    byte[] data = new byte[2];
                    data[0] = reader.ReadByte();
                    data[1] = reader.ReadByte();
                    // The WDB preamble won't include an NPC ID, so this first buffer need not be checked.
                    // We can go on and start doing comparisons and ignore the first buffer.

                    try
                    {
                        while (true)
                        {
                            // Shift our "register" by one bit.
                            data[0] = data[1];
                            data[1] = reader.ReadByte();

                            if (data[0] == npc[0]
                                && data[1] == npc[1])
                            {
                                reader.Close();
                                stream.Close();
                                return true;
                            }
                        }

                    }
                    catch (EndOfStreamException)
                    {
                        reader.Close();
                        stream.Close();
                        return false;
                    }

                }
            }

        }

        private static bool SendMailNotification(NPC FoundNPC)
        {
            // Construct Message
            MailMessage msg = new MailMessage();
            msg.From = new MailAddress(user.getUser());
            msg.To.Add(strRecipientAddress);
            msg.Subject = FoundNPC.getName() + " FOUND!";
            msg.Body = FoundNPC.getName() + " (" + FoundNPC.getID().ToString("D") + ") has been cached!";
            msg.IsBodyHtml = false;
            msg.Priority = MailPriority.Normal;

            // Send the Message
            SmtpClient client = new SmtpClient();
            client.Host = server.getHostName();
            client.Port = server.getPort();
            client.DeliveryMethod = SmtpDeliveryMethod.Network;
            client.EnableSsl = server.UseSSL();
            client.UseDefaultCredentials = false;
            //client.Timeout = 10000;

            CredentialCache cache = new CredentialCache();
            cache.Add(new Uri("http://" + server.getHostName()), "Basic", new NetworkCredential(user.getUser(), user.getPassword()));
            client.Credentials = cache.GetCredential(new Uri("http://" + server.getHostName()), "Basic");

            ConsoleLogMessage("Sending mail alert...", ConsoleMessage.INFO);
            client.Send(msg);

            return true;

        }

        private static bool DispatchedNotification(NPC thisNPC)
        {
            return DispatchedList.Contains(thisNPC);
        }

        public static String getLoggingStamp()
        {
            return "[" + DateTime.Now.ToString("HH:mm:ss") + "]";
        }

        public static void ConsoleLogMessage(String msg, ConsoleMessage type)
        {
            Console.Out.Write(getLoggingStamp() + " ");
            switch (type)
            {
                case ConsoleMessage.INFO:
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Out.Write("INFO: ");
                    Console.Out.WriteLine(msg);
                    break;
                case ConsoleMessage.WARN:
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Out.Write("WARN: ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Out.WriteLine(msg);
                    break;
                case ConsoleMessage.FOUND:
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Out.Write("FOUND: ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Out.WriteLine(msg);
                    break;
                case ConsoleMessage.FATAL:
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Out.Write("FATAL: ");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.Out.WriteLine(msg);

                    // FATAL messages are assumed to be truly fatal,
                    // so we'll pause until the user has seen the
                    // message and then close the application.
                    Console.Out.WriteLine("RETURN to continue...");
                    Console.In.ReadLine();
                    Environment.Exit(1);
                    break;
            }

        }

    }

    class NPC
    {

        private UInt16 intID;
        private String Name;

        public NPC(UInt16 intID, String NPCName)
        {
            this.intID = intID;
            this.Name = NPCName;
        }

        public NPC(XmlNode node)
        {
            this.intID = Convert.ToUInt16(node.Attributes["id"].Value.ToString(), 10);
            this.Name = node.Attributes["name"].Value.ToString();
        }

        public UInt16 getID()
        {
            return this.intID;
        }

        public String getName()
        {
            return this.Name;
        }

    }

    class SMTPServer
    {
        private String hostName;
        private int intPort;
        private Boolean useSSL;

        public SMTPServer(XmlNode serverNode)
        {
            try
            {
                this.hostName = serverNode.Attributes["host"].Value.ToString();
                this.intPort = int.Parse(serverNode.Attributes["port"].Value.ToString());
                this.useSSL = Boolean.Parse(serverNode.Attributes["useSSL"].Value.ToString());
            }
            catch (Exception)
            {
                Console.Out.WriteLine(eNPCScan.Program.getLoggingStamp() + " FATAL: Couldn't parse SMTP Server settings.");
            }
        }

        public String getHostName()
        {
            return this.hostName;
        }

        public int getPort()
        {
            return this.intPort;
        }

        public Boolean UseSSL()
        {
            return useSSL;
        }

    }

    class SMTPUser
    {
        private String strUser;
        private String strPassword;

        public SMTPUser(XmlNode userNode)
        {
            try
            {
                this.strUser = userNode.Attributes["name"].Value;
                this.strPassword = userNode.Attributes["password"].Value;
            }
            catch (Exception)
            {
                eNPCScan.Program.ConsoleLogMessage("Couldn't parse the SMTP User settings.", Program.ConsoleMessage.FATAL);
            }
        }

        public String getUser()
        {
            return strUser;
        }

        public String getPassword()
        {
            return strPassword;
        }

    }

}