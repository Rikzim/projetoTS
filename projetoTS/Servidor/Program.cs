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
using System.Data.SqlClient;

namespace Servidor
{
    class Program
    {
        // Variáveis de servidor e estado
        static TcpListener server;                                                     // Servidor TCP para aceitar conexões
        static List<TcpClient> clientes = new List<TcpClient>();                      // Lista de clientes conectados
        static Dictionary<string, string> chavesPublicas = new Dictionary<string, string>();        // Armazena chaves públicas RSA para AES por username
        static Dictionary<string, string> chavesAssinatura = new Dictionary<string, string>();      // Armazena chaves públicas RSA para assinatura por username
        static Dictionary<string, Aes> chavesAES = new Dictionary<string, Aes>();                   // Armazena chaves AES por username
        static Dictionary<TcpClient, string> clientesUsernames = new Dictionary<TcpClient, string>();// Mapeia clientes para usernames
        static object lockObj = new object();                                         // Lock para acesso thread-safe às coleções
        static RSACryptoServiceProvider rsaServidor = new RSACryptoServiceProvider(2048); // RSA do servidor

        // Método principal que inicia o servidor
        static void Main(string[] args)
        {
            try
            {
                InicializarBancoDados();

                server = new TcpListener(IPAddress.Any, 12345);
                server.Start();
                Console.WriteLine("[Servidor] A ouvir na porta 12345...");

                while (true)
                {
                    TcpClient client = server.AcceptTcpClient();
                    Console.WriteLine("[Servidor] Cliente conectado.");
                    new Thread(TratarCliente).Start(client);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro Fatal] {ex.Message}");
                Console.WriteLine("Pressione qualquer tecla para sair...");
                Console.ReadKey();
            }
        }

        // Gerencia a comunicação com um cliente específico
        static void TratarCliente(object obj)
        {
            TcpClient cliente = (TcpClient)obj;
            NetworkStream ns = cliente.GetStream();
            ProtocolSI protocolo = new ProtocolSI();
            string username = "";
            bool chaveAESEnviada = false;

            try
            {
                // Set read timeout to prevent infinite blocking
                cliente.ReceiveTimeout = 30000; // 30 seconds timeout

                while (cliente.Connected)
                {
                    // Check if there's data available before attempting to read
                    if (ns.DataAvailable)
                    {
                        int bytesRead = ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                        if (bytesRead == 0)
                        {
                            // Connection was closed gracefully
                            break;
                        }

                        ProtocolSICmdType cmdType = protocolo.GetCmdType();

                        switch (cmdType)
                        {
                            case ProtocolSICmdType.USER_OPTION_1:
                                byte[] msgAuth = protocolo.Make(ProtocolSICmdType.DATA, "utilizador");
                                ns.Write(msgAuth, 0, msgAuth.Length);
                                break;

                            case ProtocolSICmdType.DATA:
                                string dados = protocolo.GetStringFromData();
                                ProcessarDados(dados, ref username, cliente, ns, protocolo, ref chaveAESEnviada);
                                break;

                            case ProtocolSICmdType.EOT:
                                NotificarDesconexao(username, cliente);
                                return;
                        }
                    }
                    else
                    {
                        // Add a small delay to prevent CPU spinning
                        Thread.Sleep(100);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro1] {ex.Message}");
            }
            finally
            {
                FinalizarConexao(cliente, username);
            }
        }

        // Processa os dados recebidos do cliente
        static void ProcessarDados(string dados, ref string username, TcpClient cliente, NetworkStream ns, ProtocolSI protocolo, ref bool chaveAESEnviada)
        {
            // Verifica se é login ou registro
            if (dados.StartsWith("LOGIN|"))
            {
                ProcessarLogin(dados, ns, protocolo);
                return;
            }
            else if (dados.StartsWith("REGISTER|"))
            {
                ProcessarRegistro(dados, ns, protocolo);
                return;
            }

            // Processamento do chat
            if (string.IsNullOrEmpty(username))
            {
                IniciarNovoCliente(dados, cliente, ns, protocolo, ref username);
            }
            else if (!chavesPublicas.ContainsKey(username))
            {
                ReceberChavePublicaAES(dados, username, ns, protocolo);
            }
            else if (!chavesAssinatura.ContainsKey(username))
            {
                ReceberChaveAssinatura(dados, username, ns, protocolo, ref chaveAESEnviada);
            }
            else if (chaveAESEnviada)
            {
                ProcessarMensagemComAssinatura(username, dados, cliente);
            }
        }

        // Processa tentativa de login
        private static void ProcessarLogin(string dados, NetworkStream ns, ProtocolSI protocolo)
        {
            string[] partes = dados.Split('|');
            if (partes.Length != 3)
            {
                EnviarResposta("LOGIN_FAIL", ns, protocolo);
                return;
            }

            string username = partes[1];
            string password = partes[2];

            try
            {
                using (SqlConnection conn = new SqlConnection(ObterStringConexao()))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand("SELECT * FROM Users WHERE Username = @username", conn))
                    {
                        cmd.Parameters.AddWithValue("@username", username);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                EnviarResposta("LOGIN_FAIL", ns, protocolo);
                                return;
                            }

                            byte[] saltedPasswordHashStored = (byte[])reader["SaltedPasswordHash"];
                            byte[] saltStored = (byte[])reader["Salt"];

                            byte[] saltedPasswordHash = GenerateSaltedHash(password, saltStored);
                            bool loginOk = saltedPasswordHash.SequenceEqual(saltedPasswordHashStored);

                            EnviarResposta(loginOk ? "LOGIN_OK" : "LOGIN_FAIL", ns, protocolo);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro no login] {ex.Message}");
                EnviarResposta("LOGIN_FAIL", ns, protocolo);
            }
        }

        // Processa tentativa de registro
        private static void ProcessarRegistro(string dados, NetworkStream ns, ProtocolSI protocolo)
        {
            string[] partes = dados.Split('|');
            if (partes.Length != 3)
            {
                EnviarResposta("REGISTER_FAIL", ns, protocolo);
                return;
            }

            string username = partes[1];
            string password = partes[2];

            try
            {
                byte[] salt = GenerateSalt(8);
                byte[] saltedPasswordHash = GenerateSaltedHash(password, salt);

                using (SqlConnection conn = new SqlConnection(ObterStringConexao()))
                {
                    conn.Open();

                    using (SqlCommand cmdCheck = new SqlCommand("SELECT COUNT(*) FROM Users WHERE Username = @username", conn))
                    {
                        cmdCheck.Parameters.AddWithValue("@username", username);
                        if ((int)cmdCheck.ExecuteScalar() > 0)
                        {
                            EnviarResposta("REGISTER_FAIL_USERNAME_EXISTS", ns, protocolo);
                            return;
                        }
                    }

                    string sql = @"INSERT INTO Users (Username, SaltedPasswordHash, Salt) 
                                 VALUES (@username, @saltedPasswordHash, @salt)";

                    using (SqlCommand cmd = new SqlCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@username", username);
                        cmd.Parameters.AddWithValue("@saltedPasswordHash", saltedPasswordHash);
                        cmd.Parameters.AddWithValue("@salt", salt);

                        int linhas = cmd.ExecuteNonQuery();
                        EnviarResposta(linhas > 0 ? "REGISTER_OK" : "REGISTER_FAIL", ns, protocolo);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro no registro] {ex.Message}");
                EnviarResposta("REGISTER_FAIL", ns, protocolo);
            }
        }

        // Registra um novo cliente no chat
        static void IniciarNovoCliente(string dados, TcpClient cliente, NetworkStream ns, ProtocolSI protocolo, ref string username)
        {
            username = dados;
            lock (lockObj)
            {
                clientes.Add(cliente);
                clientesUsernames[cliente] = username;
            }
            Console.WriteLine($"[Servidor] Utilizador identificado: {username}");
            byte[] msg = protocolo.Make(ProtocolSICmdType.DATA, "chave pública");
            ns.Write(msg, 0, msg.Length);
        }

        // Recebe chave pública RSA para AES
        static void ReceberChavePublicaAES(string dados, string username, NetworkStream ns, ProtocolSI protocolo)
        {
            chavesPublicas[username] = dados;
            Console.WriteLine($"[Servidor] Chave pública (AES) recebida de {username}");
            byte[] msg = protocolo.Make(ProtocolSICmdType.DATA, "chave assinatura");
            ns.Write(msg, 0, msg.Length);
        }

        // Recebe chave pública RSA para assinatura
        static void ReceberChaveAssinatura(string dados, string username, NetworkStream ns, ProtocolSI protocolo, ref bool chaveAESEnviada)
        {
            chavesAssinatura[username] = dados;
            Console.WriteLine($"[Servidor] Chave pública (assinatura) recebida de {username}");
            EnviarChaveAES(username, ns, protocolo);
            chaveAESEnviada = true;
        }

        // Processa mensagem assinada
        static void ProcessarMensagemComAssinatura(string remetente, string dadosRecebidos, TcpClient clienteRemetente)
        {
            try
            {
                string[] partes = dadosRecebidos.Split(new[] { "||" }, StringSplitOptions.None);
                if (partes.Length != 2)
                {
                    Console.WriteLine($"[ERRO] Formato de mensagem inválido de {remetente}");
                    return;
                }

                string mensagemDecifrada = DecifrarMensagem(remetente, partes[0]);
                bool assinaturaValida = VerificarAssinatura(remetente, mensagemDecifrada, partes[1]);

                Console.WriteLine($"[{(assinaturaValida ? "✓" : "✗")} Mensagem de {remetente}]: {mensagemDecifrada}");

                if (assinaturaValida)
                {
                    string mensagemCompleta = $"[{remetente}]: {mensagemDecifrada}";
                    EnviarParaTodos(mensagemCompleta, clienteRemetente, remetente);
                }
                else
                {
                    Console.WriteLine($"[AVISO] Assinatura inválida de {remetente}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro ao processar mensagem] {ex.Message}");
            }
        }

        // Verifica assinatura digital
        static bool VerificarAssinatura(string username, string mensagem, string assinaturaBase64)
        {
            try
            {
                if (!chavesAssinatura.ContainsKey(username))
                {
                    Console.WriteLine($"[ERRO] Chave de assinatura não encontrada para {username}");
                    return false;
                }

                using (var rsaVerify = new RSACryptoServiceProvider())
                {
                    rsaVerify.FromXmlString(chavesAssinatura[username]);
                    byte[] dados = Encoding.UTF8.GetBytes(mensagem);
                    byte[] assinatura = Convert.FromBase64String(assinaturaBase64);
                    return rsaVerify.VerifyData(dados, CryptoConfig.MapNameToOID("SHA256"), assinatura);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro ao verificar assinatura] {ex.Message}");
                return false;
            }
        }

        // Envia mensagem para todos os clientes
        static void EnviarParaTodos(string mensagem, TcpClient remetente, string usernameRemetente)
        {
            var protocolo = new ProtocolSI();
            lock (lockObj)
            {
                foreach (var cli in clientes.Where(c => c != remetente))
                {
                    try
                    {
                        string usernameDestino = ObterUsername(cli);
                        if (string.IsNullOrEmpty(usernameDestino)) continue;

                        NetworkStream ns = cli.GetStream();
                        string msgCifrada = CifrarMensagem(usernameDestino, mensagem);
                        byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, msgCifrada);
                        ns.Write(dados, 0, dados.Length);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Erro ao enviar mensagem] {ex.Message}");
                    }
                }
            }
        }

        // Obtém username do cliente
        static string ObterUsername(TcpClient cliente)
        {
            lock (lockObj)
            {
                return clientesUsernames.ContainsKey(cliente) ? clientesUsernames[cliente] : null;
            }
        }

        // Envia chave AES para cliente
        static void EnviarChaveAES(string username, NetworkStream ns, ProtocolSI protocolo)
        {
            var aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();
            chavesAES[username] = aes;

            using (var rsaCliente = new RSACryptoServiceProvider())
            {
                rsaCliente.FromXmlString(chavesPublicas[username]);
                string resposta = Convert.ToBase64String(rsaCliente.Encrypt(aes.Key, false)) +
                              "|" +
                              Convert.ToBase64String(rsaCliente.Encrypt(aes.IV, false));

                byte[] packet = protocolo.Make(ProtocolSICmdType.DATA, resposta);
                ns.Write(packet, 0, packet.Length);
            }

            Console.WriteLine($"[Servidor] Chave AES enviada para {username}");
        }

        // Decifra mensagem com AES
        static string DecifrarMensagem(string username, string msgCifradaBase64)
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username];
            byte[] msgCifrada = Convert.FromBase64String(msgCifradaBase64);

            using (var ms = new MemoryStream())
            using (var cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
            {
                cs.Write(msgCifrada, 0, msgCifrada.Length);
                cs.FlushFinalBlock();
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        // Cifra mensagem com AES
        static string CifrarMensagem(string username, string mensagem)
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username];
            byte[] msgBytes = Encoding.UTF8.GetBytes(mensagem);

            using (var ms = new MemoryStream())
            using (var cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(msgBytes, 0, msgBytes.Length);
                cs.FlushFinalBlock();
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // Notifica desconexão
        static void NotificarDesconexao(string username, TcpClient cliente)
        {
            Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
            EnviarParaTodos($"[{username}] Desconectou-se", cliente, username);
        }

        // Finaliza conexão do cliente
        // In your server code, modify FinalizarConexao:
        static void FinalizarConexao(TcpClient cliente, string username)
        {
            try
            {
                // Remove from collections first
                lock (lockObj)
                {
                    clientes.Remove(cliente);
                    clientesUsernames.Remove(cliente);

                    // Clean up crypto resources
                    if (!string.IsNullOrEmpty(username))
                    {
                        if (chavesAES.ContainsKey(username))
                        {
                            chavesAES[username].Dispose();
                            chavesAES.Remove(username);
                        }

                        chavesPublicas.Remove(username);
                        chavesAssinatura.Remove(username);
                    }
                }

                // Close client resources
                try
                {
                    if (cliente.Connected)
                    {
                        NetworkStream ns = cliente.GetStream();
                        ns.Close();
                        ns.Dispose();
                    }
                }
                catch { } // Ignore errors during cleanup

                cliente.Close();
                cliente.Dispose();

                // Notify disconnect
                if (!string.IsNullOrEmpty(username))
                {
                    Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro ao finalizar conexão] {ex.Message}");
            }
        }

        // Envia resposta ao cliente
        private static void EnviarResposta(string mensagem, NetworkStream ns, ProtocolSI protocolo)
        {
            byte[] resposta = protocolo.Make(ProtocolSICmdType.DATA, mensagem);
            ns.Write(resposta, 0, resposta.Length);
        }

        // Obtém string de conexão do banco de dados
        private static string ObterStringConexao()
        {
            // Get the executable directory
            string exePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
            string dbFilePath = Path.Combine(exePath, "PrivyChat.mdf");

            // Make sure database directory exists
            Directory.CreateDirectory(Path.GetDirectoryName(dbFilePath));

            return $@"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename={dbFilePath};Initial Catalog=PrivyChat;Integrated Security=True;MultipleActiveResultSets=true";
        }

        // Gera salt para senha
        private static byte[] GenerateSalt(int size)
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                byte[] buff = new byte[size];
                rng.GetBytes(buff);
                return buff;
            }
        }

        // Gera hash da senha com salt
        private static byte[] GenerateSaltedHash(string plainText, byte[] salt)
        {
            using (var rfc2898 = new Rfc2898DeriveBytes(plainText, salt, 1000))
            {
                return rfc2898.GetBytes(32);
            }
        }

        private static void InicializarBancoDados()
        {
            try
            {
                string exePath = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                string dbFilePath = Path.Combine(exePath, "PrivyChat.mdf");

                // If database doesn't exist, create it
                if (!File.Exists(dbFilePath))
                {
                    using (var conn = new SqlConnection(@"Data Source=(LocalDB)\MSSQLLocalDB;Integrated Security=True"))
                    {
                        conn.Open();

                        string createDbCmd = $@"CREATE DATABASE PrivyChat ON PRIMARY 
                    (NAME = PrivyChat_Data, 
                     FILENAME = '{dbFilePath}')";

                        using (var cmd = new SqlCommand(createDbCmd, conn))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Create tables
                        string connectionString = ObterStringConexao();
                        using (var dbConn = new SqlConnection(connectionString))
                        {
                            dbConn.Open();
                            string createTableCmd = @"
                        IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='Users' and xtype='U')
                        CREATE TABLE Users (
                            Id INT PRIMARY KEY IDENTITY(1,1),
                            Username NVARCHAR(50) NOT NULL UNIQUE,
                            SaltedPasswordHash VARBINARY(MAX) NOT NULL,
                            Salt VARBINARY(MAX) NOT NULL
                        )";

                            using (var cmd = new SqlCommand(createTableCmd, dbConn))
                            {
                                cmd.ExecuteNonQuery();
                            }
                        }
                    }
                    Console.WriteLine("[Servidor] Banco de dados criado com sucesso!");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro] Falha ao inicializar banco de dados: {ex.Message}");
                throw;
            }
        }
    }
}