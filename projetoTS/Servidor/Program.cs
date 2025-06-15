using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using EI.SI;
using System.Security.Cryptography;
using System.Linq;

namespace Servidor
{
    class Program
    {
        static Dictionary<string, TcpClient> clientes = new Dictionary<string, TcpClient>();// dicionario para armazenar os clientes conectados
        static Dictionary<string, string> chavesPublicasAES = new Dictionary<string, string>(); // dicionario para armazenar as chaves publicas AES dos clientes
        static Dictionary<string, string> chavesPublicasAssinatura = new Dictionary<string, string>(); // dicionario para armazenar as chaves publicas de assinatura dos clientes
        static Dictionary<string, Aes> chavesAES = new Dictionary<string, Aes>(); // dicionario para armazenar as chaves privadas AES dos clientes
        static object lockObj = new object(); // objeto que garante que os dicionarios nao sejam acedido por mais de uma thread ao mesmo tempo

        // Método principal que inicia o servidor
        static void Main(string[] args)
        {
            InicializarBD();// inicializa a base de dados

            TcpListener server = new TcpListener(IPAddress.Any, 12345);
            server.Start();
            Console.WriteLine("[Servidor] A ouvir na porta 12345...");

            // Loop principal que aceita novas conexões
            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                Thread t = new Thread(() => HandleClient(client)); // cria uma nova thread para lidar com o cliente
                t.Start();
            }
        }

        static void HandleClient(TcpClient client) // função para lidar com o cliente conectado
        {
            // Cria o NetworkStream e o protocolo
            NetworkStream ns = client.GetStream();
            ProtocolSI protocolo = new ProtocolSI();
            string username = "";
            bool chaveAESEnviada = false;

            try
            {
                while (true)
                {
                    // Lê dados do cliente
                    ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                    ProtocolSICmdType cmdType = protocolo.GetCmdType();

                    if (cmd == ProtocolSICmdType.DATA)// verifica se o comando é de dados
                    {
                        case ProtocolSICmdType.USER_OPTION_1: // Pedido inicial de autenticação
                            byte[] msgAuth = protocolo.Make(ProtocolSICmdType.DATA, "utilizador");
                            ns.Write(msgAuth, 0, msgAuth.Length);
                            break;

                        if (msg.StartsWith("LOGIN|")) // verifica se a mensagem e de login
                        {
                            username = ProcessarLogin(msg, ns, protocolo); // processa o login
                            if (!string.IsNullOrEmpty(username))
                                Console.WriteLine("Utilizador " + username + " fez login.");
                        }
                        else if (msg.StartsWith("REGISTER|")) // verifica se a mensagem e de registro
                        {
                            username = ProcessarRegistro(msg, ns, protocolo); // processa o registro
                            if (!string.IsNullOrEmpty(username))
                                Console.WriteLine("Utilizador " + username + " registado.");
                        }
                        else if (msg.StartsWith("CHAVE_PUBLICA|")) // verifica se a mensagem e de chave publica
                        {
                            string[] parts = msg.Split('|'); // divide a mensagem em partes
                            if (parts.Length == 3)
                            {
                                if (!chavesPublicasAES.ContainsKey(parts[1])) 
                                {
                                    lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread acede ao dicionario
                                    {
                                        chavesPublicasAES[parts[1]] = parts[2];// guarda a chave publica aes do cliente no dicionario
                                    }
                                    ProtocolSI proto = new ProtocolSI();
                                    byte[] resp = proto.Make(ProtocolSICmdType.DATA, "chave assinatura"); // envia uma mensagem de confirmacao
                                    ns.Write(resp, 0, resp.Length);
                                    continue;
                                }
                                else if (!chavesPublicasAssinatura.ContainsKey(parts[1]))
                                {
                                    lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread acede ao dicionario
                                    {
                                        chavesPublicasAssinatura[parts[1]] = parts[2];// guarda a chave publica de assinatura do cliente no dicionario
                                    }
                                    EnviarChaveAES(parts[1], ns, protocolo);// envia a chave aes gerada pelo servidor para o cliente
                                    chaveAESEnviada = true;
                                    lock (lockObj)
                                    {
                                        clientes[parts[1]] = client;// adiciona o cliente ao dicionario de clientes conectados
                                    }
                                    Console.WriteLine("Chave pública de assinatura de " + parts[1] + " recebida.");
                                    continue;
                                }
                            }
                        }
                        else if (chaveAESEnviada && !string.IsNullOrEmpty(username)) // verifica se a chave foi enviada e se o username existe
                        {
                            // Mensagem cifrada e assinada: encrypted||signature
                            string[] parts = msg.Split(new[] { "||" }, StringSplitOptions.None);
                            if (parts.Length == 2 && chavesAES.ContainsKey(username) && chavesPublicasAssinatura.ContainsKey(username))
                            {
                                string decrypted = DecifrarMensagem(username, parts[0]); // decifra a mensagem
                                bool valid = VerificarAssinatura(username, decrypted, parts[1]); // verfica a assinatura da mensagem
                                string label = "[" + username + "]: " + decrypted + (valid ? " ✓" : " ✗");
                                Console.WriteLine(label);
                                EnviarParaTodos(label, username); // envia a mensagem para todos
                            }
                        }
                    }
                    else if (cmd == ProtocolSICmdType.EOT)// verifica se o comando e de fim de transmissao
                    {
                        Console.WriteLine("Utilizador " + username + " desconectado.");
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro] {ex.Message}");
            }
            finally
            {
                lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread acede ao dicionario
                {
                    // remove o cliente desconectado
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

        static void EnviarParaTodos(string msg, string remetente)// funcao para enviar a mensagem para todos
        {
            if (string.IsNullOrEmpty(username)) // Primeira mensagem - username
            {
                IniciarNovoCliente(dados, cliente, ns, protocolo, ref username);
            }
            else if (!chavesPublicas.ContainsKey(username)) // Segunda mensagem - chave pública AES
            {
                ReceberChavePublicaAES(dados, username, ns, protocolo);
            }
            else if (!chavesAssinatura.ContainsKey(username)) // Terceira mensagem - chave pública assinatura
            {
                ReceberChaveAssinatura(dados, username, ns, protocolo, ref chaveAESEnviada);
            }
            else if (chaveAESEnviada) // Mensagens subsequentes - chat normal
            {
                ProcessarMensagemComAssinatura(username, dados, cliente);
            }
        }

            lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread acede ao dicionario
            {
                foreach (KeyValuePair<string, TcpClient> par in clientes) // ciclo para aceder a todos clientes conectados
                {
                    if (par.Key != remetente) // verifica se o cliente nao e o rementente da mensagem
                    {
                        try
                        {
                            par.Value.GetStream().Write(dados, 0, dados.Length);// envia a mensagem
                        }
                    }
                }
            }
        }

        static void Enviar(NetworkStream ns, ProtocolSI protocolo, string msg)// funcao para enviar mensagem
        {
            lock (lockObj)
            {
                return clientesUsernames.ContainsKey(cliente) ? clientesUsernames[cliente] : null;
            }
        }

        static void EnviarChaveAES(string username, NetworkStream ns, ProtocolSI protocolo) // funcao para enviar a chave aes para o cliente
        {
            // Cria uma nova chave AES
            Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();
            chavesAES[username] = aes;

            using (var rsaCliente = new RSACryptoServiceProvider())
            {
                rsaCliente.FromXmlString(chavesPublicasAES[username]); // busca a chave publica AES do cliente
                string resposta = Convert.ToBase64String(rsaCliente.Encrypt(aes.Key, false)) + "|" + Convert.ToBase64String(rsaCliente.Encrypt(aes.IV, false));

            string resposta = Convert.ToBase64String(rsaCliente.Encrypt(aes.Key, false)) +
                          "|" +
                          Convert.ToBase64String(rsaCliente.Encrypt(aes.IV, false));

            byte[] packet = protocolo.Make(ProtocolSICmdType.DATA, resposta);
            ns.Write(packet, 0, packet.Length);

            rsaCliente.Dispose();
            Console.WriteLine($"[Servidor] Chave AES enviada para {username}");
        }

        static string DecifrarMensagem(string username, string msgCifradaBase64) // funcao para decifrar mensagem
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username]; // busca a chave aes do utilizador
            byte[] msgCifrada = Convert.FromBase64String(msgCifradaBase64);

            using (MemoryStream ms = new MemoryStream())
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write)) // decifra a mensagem
            {
                cs.Write(msgCifrada, 0, msgCifrada.Length);
                cs.FlushFinalBlock();// garante que todos os dados sejam escritos corretamente
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        static bool VerificarAssinatura(string username, string mensagem, string assinaturaBase64)// funcao para verificar a assinatura da mensagem
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username];
            byte[] msgBytes = Encoding.UTF8.GetBytes(mensagem);

            using (MemoryStream ms = new MemoryStream())
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread acede ao dicionario
                {
                    if (!chavesPublicasAssinatura.ContainsKey(username))
                        return false;
                    RSACryptoServiceProvider rsa = new RSACryptoServiceProvider();
                    rsa.FromXmlString(chavesPublicasAssinatura[username]); // busca a chave de assinatura do cliente
                    byte[] data = Encoding.UTF8.GetBytes(mensagem);
                    byte[] assinatura = Convert.FromBase64String(assinaturaBase64);
                    bool result = rsa.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), assinatura); // verifica a assinatura
                    rsa.Dispose(); 
                    return result;
                }
            }
            catch
            {
                return false;
            }
        }

        static string ProcessarLogin(string dados, NetworkStream ns, ProtocolSI protocolo) // funcao para processar o login do utilizador
        {
            string[] partes = dados.Split('|'); // divide a mensagem em partes
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
                    using (SqlCommand cmd = new SqlCommand("SELECT SaltedPasswordHash, Salt FROM Users WHERE Username = @u", conn)) // consulta a base de dados para obter o hash e o salt do utilizador
                    {
                        cmd.Parameters.AddWithValue("@u", username);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                Enviar(ns, protocolo, "LOGIN_FAIL"); // envia mensagem de falha se o utilizador nao existir
                                return "";
                            }
                            byte[] hashDB = (byte[])reader["SaltedPasswordHash"];
                            byte[] salt = (byte[])reader["Salt"];
                            byte[] hash = GerarHash(password, salt);

                            if (SequenceEqual(hash, hashDB)) // verifica se o hash gerado com a senha e igual ao hash da base de dados
                            {
                                Enviar(ns, protocolo, "LOGIN_OK"); // envia mensagem de sucesso
                                return username;
                            }
                            else
                            {
                                Enviar(ns, protocolo, "LOGIN_FAIL"); // envia mensagem de falha
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

        // Finaliza a conexão de um cliente e limpa seus recursos
        static void FinalizarConexao(TcpClient cliente, string username)
        {
            string[] partes = dados.Split('|'); // divide a mensagem em partes
            if (partes.Length != 3)
            {
                clientes.Remove(cliente);
                clientesUsernames.Remove(cliente);
            }
            if (!string.IsNullOrEmpty(username))
            {
                using (SqlConnection conn = new SqlConnection(ObterStringConexao()))
                {
                    conn.Open();
                  
                    using (SqlCommand check = new SqlCommand("SELECT COUNT(*) FROM Users WHERE Username=@u", conn)) // verifica se o utilizador ja existe
                    {
                        check.Parameters.AddWithValue("@u", username);
                        if ((int)check.ExecuteScalar() > 0)
                        {
                            Enviar(ns, protocolo, "REGISTER_FAIL_USERNAME_EXISTS"); // envia mensagem de falha se o utilizador ja existir
                            return "";
                        }
                    }

                    byte[] salt = GerarSalt();
                    byte[] hash = GerarHash(password, salt);

                    using (SqlCommand cmd = new SqlCommand("INSERT INTO Users (Username, SaltedPasswordHash, Salt) VALUES (@u,@h,@s)", conn)) // insere o utilizador na base de dados
                    {
                        cmd.Parameters.AddWithValue("@u", username);
                        cmd.Parameters.AddWithValue("@h", hash);
                        cmd.Parameters.AddWithValue("@s", salt);

                        if (cmd.ExecuteNonQuery() > 0)
                        {
                            Enviar(ns, protocolo, "REGISTER_OK"); // envia mensagem de sucesso
                            return username;
                        }
                        else
                        {
                            Enviar(ns, protocolo, "REGISTER_FAIL"); // envia mensagem de falha
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

        static string ObterStringConexao() // funcao para obter a string de conexao com a base de dados
        {
            string db = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrivyChat.mdf");
            return @"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename=" + db + ";Integrated Security=True";
        }

        static byte[] GerarSalt()// funcao para gerar um salt aleatorio
        {
            byte[] salt = new byte[8];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider()) 
            {
                rng.GetBytes(salt);
            }
            return salt;
        }

        static byte[] GerarHash(string senha, byte[] salt)// funcao para gerar o hash da senha com o salt
        {
            using (Rfc2898DeriveBytes hash = new Rfc2898DeriveBytes(senha, salt, 1000))
            {
                return hash.GetBytes(32);
            }
        }

        static bool SequenceEqual(byte[] a, byte[] b)// funcao para comparar dois arrays de bytes
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }

        static void InicializarBD() // funcao para inicializar a base de dados, utilizado apenas se a base de dados nao existir
        {
            string db = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrivyChat.mdf");
            if (!File.Exists(db)) // verifica se a base de dados ja existe
            {
                using (SqlConnection conn = new SqlConnection(@"Data Source=(LocalDB)\MSSQLLocalDB;Integrated Security=True"))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand("CREATE DATABASE PrivyChat ON PRIMARY (NAME = PrivyChat_Data, FILENAME = '" + db + "')", conn))// cria a base de dados
                    {
                        cmd.ExecuteNonQuery();
                    }
                }
                using (SqlConnection dbConn = new SqlConnection(ObterStringConexao()))
                {
                    dbConn.Open();
                    string tableCmd = "IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Users' and xtype='U') CREATE TABLE Users (Id INT PRIMARY KEY IDENTITY(1,1), Username NVARCHAR(50) NOT NULL UNIQUE, SaltedPasswordHash VARBINARY(MAX) NOT NULL, Salt VARBINARY(MAX) NOT NULL)"; // cria a tabela Users se nao existir
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