using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using EI.SI;

namespace Servidor
{
    class Program
    {
        static Dictionary<string, TcpClient> clientes = new Dictionary<string, TcpClient>();
        static Dictionary<string, string> chavesPublicasAES = new Dictionary<string, string>();
        static Dictionary<string, string> chavesPublicasAssinatura = new Dictionary<string, string>();
        static Dictionary<string, Aes> chavesAES = new Dictionary<string, Aes>();
        static object lockObj = new object();

        static void Main(string[] args)
        {
            InicializarBD();

            TcpListener server = new TcpListener(IPAddress.Any, 12345);
            server.Start();
            Console.WriteLine("Servidor iniciado...");

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                Thread t = new Thread(() => HandleClient(client));
                t.Start();
            }
        }

        static void HandleClient(TcpClient client)
        {
            NetworkStream ns = client.GetStream();
            ProtocolSI protocolo = new ProtocolSI();
            string username = "";
            bool chaveAESEnviada = false;

            try
            {
                while (true)
                {
                    ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                    ProtocolSICmdType cmd = protocolo.GetCmdType();

                    if (cmd == ProtocolSICmdType.DATA)
                    {
                        string msg = protocolo.GetStringFromData();

                        if (msg.StartsWith("LOGIN|"))
                        {
                            username = ProcessarLogin(msg, ns, protocolo);
                            if (!string.IsNullOrEmpty(username))
                                Console.WriteLine("Utilizador " + username + " fez login.");
                        }
                        else if (msg.StartsWith("REGISTER|"))
                        {
                            username = ProcessarRegistro(msg, ns, protocolo);
                            if (!string.IsNullOrEmpty(username))
                                Console.WriteLine("Utilizador " + username + " registado.");
                        }
                        else if (msg.StartsWith("CHAVE_PUBLICA|"))
                        {
                            string[] parts = msg.Split('|');
                            if (parts.Length == 3)
                            {
                                if (!chavesPublicasAES.ContainsKey(parts[1]))
                                {
                                    lock (lockObj)
                                    {
                                        chavesPublicasAES[parts[1]] = parts[2];
                                    }
                                    ProtocolSI proto = new ProtocolSI();
                                    byte[] resp = proto.Make(ProtocolSICmdType.DATA, "chave assinatura");
                                    ns.Write(resp, 0, resp.Length);
                                    continue;
                                }
                                else if (!chavesPublicasAssinatura.ContainsKey(parts[1]))
                                {
                                    lock (lockObj)
                                    {
                                        chavesPublicasAssinatura[parts[1]] = parts[2];
                                    }
                                    EnviarChaveAES(parts[1], ns, protocolo);
                                    chaveAESEnviada = true;
                                    lock (lockObj)
                                    {
                                        clientes[parts[1]] = client;
                                    }
                                    Console.WriteLine("Chave pública de assinatura de " + parts[1] + " recebida.");
                                    continue;
                                }
                            }
                        }
                        else if (chaveAESEnviada && !string.IsNullOrEmpty(username))
                        {
                            // Mensagem cifrada e assinada: encrypted||signature
                            string[] parts = msg.Split(new[] { "||" }, StringSplitOptions.None);
                            if (parts.Length == 2 && chavesAES.ContainsKey(username) && chavesPublicasAssinatura.ContainsKey(username))
                            {
                                string decrypted = DecifrarMensagem(username, parts[0]);
                                bool valid = VerificarAssinatura(username, decrypted, parts[1]);
                                string label = "[" + username + "]: " + decrypted + (valid ? " ✓" : " ✗");
                                Console.WriteLine(label);
                                EnviarParaTodos(label, username);
                            }
                        }
                    }
                    else if (cmd == ProtocolSICmdType.EOT)
                    {
                        Console.WriteLine("Utilizador " + username + " desconectado.");
                        break;
                    }
                }
            }
            catch (IOException ex)
            {
                Console.WriteLine("[INFO] Cliente " + username + " desconectou-se (" + ex.Message + ").");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[ERRO] " + ex.Message);
            }
            finally
            {
                lock (lockObj)
                {
                    if (!string.IsNullOrEmpty(username)) clientes.Remove(username);
                    if (!string.IsNullOrEmpty(username)) chavesPublicasAES.Remove(username);
                    if (!string.IsNullOrEmpty(username)) chavesPublicasAssinatura.Remove(username);
                    if (!string.IsNullOrEmpty(username) && chavesAES.ContainsKey(username))
                    {
                        chavesAES[username].Dispose();
                        chavesAES.Remove(username);
                    }
                }
                client.Close();
            }
        }

        static void EnviarParaTodos(string msg, string remetente)
        {
            ProtocolSI protocolo = new ProtocolSI();
            byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, msg);

            lock (lockObj)
            {
                foreach (KeyValuePair<string, TcpClient> par in clientes)
                {
                    if (par.Key != remetente)
                    {
                        try
                        {
                            par.Value.GetStream().Write(dados, 0, dados.Length);
                        }
                        catch { }
                    }
                }
            }
        }

        static void Enviar(NetworkStream ns, ProtocolSI protocolo, string msg)
        {
            byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, msg);
            ns.Write(dados, 0, dados.Length);
        }

        static void EnviarChaveAES(string username, NetworkStream ns, ProtocolSI protocolo)
        {
            // Cria uma nova chave AES
            Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();

            chavesAES[username] = aes;

            using (var rsaCliente = new RSACryptoServiceProvider())
            {
                rsaCliente.FromXmlString(chavesPublicasAES[username]);
                string resposta = Convert.ToBase64String(rsaCliente.Encrypt(aes.Key, false)) +
                              "|" +
                              Convert.ToBase64String(rsaCliente.Encrypt(aes.IV, false));

            string resposta = Convert.ToBase64String(rsaCliente.Encrypt(aes.Key, false)) +
                          "|" +
                          Convert.ToBase64String(rsaCliente.Encrypt(aes.IV, false));

            byte[] packet = protocolo.Make(ProtocolSICmdType.DATA, resposta);
            ns.Write(packet, 0, packet.Length);

            rsaCliente.Dispose();
            Console.WriteLine($"[Servidor] Chave AES enviada para {username}");
        }

        static string DecifrarMensagem(string username, string msgCifradaBase64)
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username];
            byte[] msgCifrada = Convert.FromBase64String(msgCifradaBase64);

            using (MemoryStream ms = new MemoryStream())
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            {
                cs.Write(msgCifrada, 0, msgCifrada.Length);
                cs.FlushFinalBlock();
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        static bool VerificarAssinatura(string username, string mensagem, string assinaturaBase64)
        {
            cliente.Close();
            lock (lockObj)
            {
                if (!chavesPublicasAssinatura.ContainsKey(username))
                    return false;
                RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
                rsa.FromXmlString(chavesPublicasAssinatura[username]);
                byte[] data = Encoding.UTF8.GetBytes(mensagem);
                byte[] assinatura = Convert.FromBase64String(assinaturaBase64);
                bool result = rsa.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), assinatura);
                rsa.Dispose();
                return result;
            }
            catch
            {
                return false;
            }
        }

        static string ProcessarLogin(string dados, NetworkStream ns, ProtocolSI protocolo)
        {
            string[] partes = dados.Split('|');
            if (partes.Length != 3)
            {
                Enviar(ns, protocolo, "LOGIN_FAIL");
                return "";
            }
            string username = partes[1];
            string password = partes[2];

            try
            {
                using (SqlConnection conn = new SqlConnection(ObterStringConexao()))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand("SELECT SaltedPasswordHash, Salt FROM Users WHERE Username = @u", conn))
                    {
                        cmd.Parameters.AddWithValue("@u", username);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                Enviar(ns, protocolo, "LOGIN_FAIL");
                                return "";
                            }
                            byte[] hashDB = (byte[])reader["SaltedPasswordHash"];
                            byte[] salt = (byte[])reader["Salt"];
                            byte[] hash = GerarHash(password, salt);

                            if (SequenceEqual(hash, hashDB))
                            {
                                Enviar(ns, protocolo, "LOGIN_OK");
                                return username;
                            }
                            else
                            {
                                Enviar(ns, protocolo, "LOGIN_FAIL");
                                return "";
                            }
                        }
                    }
                }
            }
            catch
            {
                Enviar(ns, protocolo, "LOGIN_FAIL");
                return "";
            }
        }

        static string ProcessarRegistro(string dados, NetworkStream ns, ProtocolSI protocolo)
        {
            string[] partes = dados.Split('|');
            if (partes.Length != 3)
            {
                Enviar(ns, protocolo, "REGISTER_FAIL");
                return "";
            }
            string username = partes[1];
            string password = partes[2];

            try
            {
                using (SqlConnection conn = new SqlConnection(ObterStringConexao()))
                {
                    conn.Open();

                    using (SqlCommand check = new SqlCommand("SELECT COUNT(*) FROM Users WHERE Username=@u", conn))
                    {
                        check.Parameters.AddWithValue("@u", username);
                        if ((int)check.ExecuteScalar() > 0)
                        {
                            Enviar(ns, protocolo, "REGISTER_FAIL_USERNAME_EXISTS");
                            return "";
                        }
                    }

                    byte[] salt = GerarSalt();
                    byte[] hash = GerarHash(password, salt);

                    using (SqlCommand cmd = new SqlCommand("INSERT INTO Users (Username, SaltedPasswordHash, Salt) VALUES (@u,@h,@s)", conn))
                    {
                        cmd.Parameters.AddWithValue("@u", username);
                        cmd.Parameters.AddWithValue("@h", hash);
                        cmd.Parameters.AddWithValue("@s", salt);

                        if (cmd.ExecuteNonQuery() > 0)
                        {
                            Enviar(ns, protocolo, "REGISTER_OK");
                            return username;
                        }
                        else
                        {
                            Enviar(ns, protocolo, "REGISTER_FAIL");
                            return "";
                        }
                    }
                }
            }
            catch
            {
                Enviar(ns, protocolo, "REGISTER_FAIL");
                return "";
            }
        }

        static string ObterStringConexao()
        {
            string db = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrivyChat.mdf");
            return @"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename=" + db + ";Integrated Security=True";
        }

        static byte[] GerarSalt()
        {
            byte[] salt = new byte[8];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }

        static byte[] GerarHash(string senha, byte[] salt)
        {
            using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(senha, salt, 1000))
            {
                return pbkdf2.GetBytes(32);
            }
        }

        static bool SequenceEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        static void InicializarBD()
        {
            string db = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrivyChat.mdf");
            if (!File.Exists(db))
            {
                using (SqlConnection conn = new SqlConnection(@"Data Source=(LocalDB)\MSSQLLocalDB;Integrated Security=True"))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand("CREATE DATABASE PrivyChat ON PRIMARY (NAME = PrivyChat_Data, FILENAME = '" + db + "')", conn))
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                using (SqlConnection dbConn = new SqlConnection(ObterStringConexao()))
                {
                    dbConn.Open();
                    string tableCmd = "IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Users' and xtype='U') CREATE TABLE Users (Id INT PRIMARY KEY IDENTITY(1,1), Username NVARCHAR(50) NOT NULL UNIQUE, SaltedPasswordHash VARBINARY(MAX) NOT NULL, Salt VARBINARY(MAX) NOT NULL)";
                    using (SqlCommand tCmd = new SqlCommand(tableCmd, dbConn))
                    {
                        tCmd.ExecuteNonQuery();
                    }
                }
                Console.WriteLine("[Servidor] BD criado.");
            }
        }
    }
}