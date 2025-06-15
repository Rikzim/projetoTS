using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
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
        static Dictionary<string, TcpClient> clientes = new Dictionary<string, TcpClient>(); // dicionario para armazenar clientes conectados
        static Dictionary<string, string> chavesPublicasAES = new Dictionary<string, string>(); // dicionario para armazenar chaves publicas AES dos clientes
        static Dictionary<string, string> chavesPublicasAssinatura = new Dictionary<string, string>(); // dicionario para armazenar chaves publicas de assinatura dos clientes
        static Dictionary<string, Aes> chavesAES = new Dictionary<string, Aes>(); // dicionario para armazenar chaves privadas AES dos clientes que serao enviadas para o cliente

        static object lockObj = new object(); // objeto para garantir que os dicionarios nao sejam modificado por varias threads ao mesmo tempo.
        static object lockLog = new object(); // objeto para garantir que o log nao seja modificado por varias threads ao mesmo tempo.

        static string caminhoLog = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log.txt"); // caminho do arquivo do log

        static void Main(string[] args)
        {
            InicializarBD(); // inicializa a base de dados, e cria a base de dados ou a tabela users se nao exitistirem
            InicializarLog(); // inicializa o sistema de log

            TcpListener server = new TcpListener(IPAddress.Any, 12345);
            server.Start();
            Log("[SERVIDOR] Servidor iniciado na porta 12345");

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                Thread t = new Thread(() => HandleClient(client)); // cria uma nova thread para lidar com o cliente conectado
                t.Start();
            }
        }

        static void HandleClient(TcpClient client) // funcao que lida com cada cliente conectado, recebendo mensagens e processando-as, e tambem enviando mensagens para todos os clientes conectados
        {
            // Inicia o protocolo e variaveis
            NetworkStream ns = client.GetStream();
            ProtocolSI protocolo = new ProtocolSI();
            string username = "";
            bool chaveAESEnviada = false;

            try
            {
                while (true)
                {
                    ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                    ProtocolSICmdType cmd = protocolo.GetCmdType(); // obtem o tipo de comando do protocolo

                    if (cmd == ProtocolSICmdType.DATA) // se o comando for de dados, processa a mensagem recebida
                    {
                        string msg = protocolo.GetStringFromData();

                        if (msg.StartsWith("LOGIN|")) // se a mensagem for de login, processa o login
                        {
                            username = ProcessarLogin(msg, ns, protocolo);
                            if (!string.IsNullOrEmpty(username))
                            {
                                Log("[INFO] Utilizador " + username + " fez login.");
                            }
                        }
                        else if (msg.StartsWith("REGISTER|")) // se a mensagem for de registro, processa o registro
                        {
                            username = ProcessarRegistro(msg, ns, protocolo);
                            if (!string.IsNullOrEmpty(username))
                            {
                                Log("[INFO] Utilizador " + username + " registado.");
                            }
                        }
                        else if (msg.StartsWith("CHAVE_PUBLICA|")) // se a mensagem for de chave publica, processa a chave publica
                        {
                            string[] parts = msg.Split('|'); // divide a mensagem em partes
                            if (parts.Length == 3)
                            {
                                if (!chavesPublicasAES.ContainsKey(parts[1])) // se a chave publica do cliente nao estiver no dicionario, adiciona
                                {
                                    lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread por vez possa modificar o dicionario
                                    {
                                        chavesPublicasAES[parts[1]] = parts[2]; // adiciona a chave publica AES do cliente
                                    }
                                    ProtocolSI proto = new ProtocolSI();
                                    byte[] resp = proto.Make(ProtocolSICmdType.DATA, "chave assinatura"); // envia uma resposta para o cliente, pedido a chave de assinatura
                                    ns.Write(resp, 0, resp.Length);
                                    continue;
                                }
                                else if (!chavesPublicasAssinatura.ContainsKey(parts[1])) // se a chave publica de assinatura do cliente nao estiver no dicionario, adiciona
                                {
                                    lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread por vez possa modificar o dicionario
                                    {
                                        chavesPublicasAssinatura[parts[1]] = parts[2]; // adiciona a chave publica de assinatura do cliente
                                    }
                                    EnviarChaveAES(parts[1], ns, protocolo); // envia a chave privada AES para o cliente
                                    chaveAESEnviada = true;
                                    lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread por vez possa modificar o dicionario
                                    {
                                        clientes[parts[1]] = client; // adiciona o cliente ao dicionario de clientes conectados
                                    }
                                    Log("[Servidor] Chave pública de assinatura de " + parts[1] + " recebida.");
                                    continue;
                                }
                            }
                        }
                        else if (chaveAESEnviada && !string.IsNullOrEmpty(username)) // verifica se a chave AES ja foi enviada e se o username nao esta vazio
                        {
                            // Mensagem cifrada e assinada: encrypted||signature
                            string[] parts = msg.Split(new[] { "||" }, StringSplitOptions.None);
                            if (parts.Length == 2 && chavesAES.ContainsKey(username) && chavesPublicasAssinatura.ContainsKey(username))
                            {
                                string decrypted = DecifrarMensagem(username, parts[0]); // decifra a mensagem usando a chave AES do cliente
                                bool valid = VerificarAssinatura(username, decrypted, parts[1]); // verifica a assinatura da mensagem usando a chave publica de assinatura do cliente
                                string label = "[" + username + "]: " + decrypted;
                                Log(label + " " + (valid ? "[Mensagem Verificada]" : "[Mensagem não Verificada]")); 
                                EnviarParaTodos(label, username); // envia a mensagem para todos os clientes conectados, exceto o remetente
                            }
                        }
                    }
                    else if (cmd == ProtocolSICmdType.EOT)
                    {
                        Log("[INFO] Utilizador " + username + " desconectado.");
                        break;
                    }
                }
            }
            catch (IOException ex)
            {
                if (!string.IsNullOrEmpty(username))
                    Log("[INFO] Utilizador " + username + " desconectou-se (" + ex.Message + ").");
            }
            catch (Exception ex)
            {
                Log("[ERRO] " + ex.Message);
            }
            finally
            {
                lock (lockObj) // bloqueia o objeto para garantir que apenas uma thread por vez possa modificar os dicionarios
                {
                    // Remove o cliente do dicionario de clientes conectados e as chaves AES e publicas associadas ao cliente
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

        static void EnviarParaTodos(string msg, string remetente) // funcao que envia uma mensagem para todos os clientes conectados, exceto o remetente
        {
            ProtocolSI protocolo = new ProtocolSI();
            byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, msg);

            lock (lockObj)
            {
                foreach (KeyValuePair<string, TcpClient> par in clientes) // ciclo para enviar a mensagem para todos os clientes conectados
                {
                    if (par.Key != remetente)
                    {
                        try
                        {
                            par.Value.GetStream().Write(dados, 0, dados.Length);
                        }
                        catch 
                        { 
                            throw new Exception("Erro ao enviar mensagem para " + par.Key);
                        }
                    }
                }
            }
        }

        static void Enviar(NetworkStream ns, ProtocolSI protocolo, string msg) // funcao que envia uma mensagem para o cliente conectado
        {
            byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, msg);
            ns.Write(dados, 0, dados.Length);
        }

        static void EnviarChaveAES(string username, NetworkStream ns, ProtocolSI protocolo) // funcao que envia a chave AES para o cliente conectado
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

                byte[] packet = protocolo.Make(ProtocolSICmdType.DATA, resposta);
                ns.Write(packet, 0, packet.Length);

                rsaCliente.Dispose();
                Log("[Servidor] Chave AES enviada para " + username);
            }
        }

        static string DecifrarMensagem(string username, string msgCifradaBase64) // funcao que decifra uma mensagem cifrada usando a chave AES do cliente
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

        static bool VerificarAssinatura(string username, string mensagem, string assinaturaBase64) // funcao que verifica a assinatura de uma mensagem usando a chave publica de assinatura do cliente
        {
            try
            {
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
            }
            catch
            {
                return false;
            }
        }

        static string ProcessarLogin(string dados, NetworkStream ns, ProtocolSI protocolo) // funcao que processa o login de um cliente, verificando se o username e password estao corretos
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
                    using (SqlCommand cmd = new SqlCommand("SELECT SaltedPasswordHash, Salt FROM Users WHERE Username = @u", conn)) // seleciona o hash e o salt do utilizador
                    {
                        cmd.Parameters.AddWithValue("@u", username);
                        using (SqlDataReader reader = cmd.ExecuteReader())
                        {
                            if (!reader.Read())
                            {
                                Enviar(ns, protocolo, "LOGIN_FAIL"); // se o utilizador nao existir, envia mensagem de falha
                                return "";
                            }
                            byte[] hashDB = (byte[])reader["SaltedPasswordHash"];
                            byte[] salt = (byte[])reader["Salt"];
                            byte[] hash = GerarHash(password, salt); // gera o hash da password usando o salt do utilizador


                            if (hash.SequenceEqual(hashDB)) //SequenceEqual(hash, hashDB) // verifica se o hash gerado da password corresponde ao hash armazenado na base de dados
                            {
                                Enviar(ns, protocolo, "LOGIN_OK"); // se o login for bem sucedido, envia mensagem de sucesso
                                return username;
                            }
                            else
                            {
                                Enviar(ns, protocolo, "LOGIN_FAIL"); // se o hash nao corresponder, envia mensagem de falha
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
            string[] partes = dados.Split('|'); // divide a mensagem em partes
            if (partes.Length != 3)
            {
                Enviar(ns, protocolo, "REGISTER_FAIL"); // se a mensagem nao tiver o formato correto, envia mensagem de falha
                return "";
            }
            string username = partes[1];
            string password = partes[2];

            try
            {
                using (SqlConnection conn = new SqlConnection(ObterStringConexao()))
                {
                    conn.Open();

                    using (SqlCommand check = new SqlCommand("SELECT COUNT(*) FROM Users WHERE Username=@u", conn)) // verifica se o username ja existe na base de dados
                    {
                        check.Parameters.AddWithValue("@u", username);
                        if ((int)check.ExecuteScalar() > 0) // verifica se o resultado e maior que 0
                        {
                            Enviar(ns, protocolo, "REGISTER_FAIL_USERNAME_EXISTS"); // se o username ja existir, envia mensagem de falha
                            return "";
                        }
                    }

                    byte[] salt = GerarSalt();
                    byte[] hash = GerarHash(password, salt); // gera o hash da password usando um salt aleatorio

                    using (SqlCommand cmd = new SqlCommand("INSERT INTO Users (Username, SaltedPasswordHash, Salt) VALUES (@u,@h,@s)", conn)) // insere o utilizador na base de dados
                    {
                        cmd.Parameters.AddWithValue("@u", username);
                        cmd.Parameters.AddWithValue("@h", hash);
                        cmd.Parameters.AddWithValue("@s", salt);

                        if (cmd.ExecuteNonQuery() > 0)
                        {
                            Enviar(ns, protocolo, "REGISTER_OK"); // se o registro for bem sucedido, envia mensagem de sucesso
                            return username;
                        }
                        else
                        {
                            Enviar(ns, protocolo, "REGISTER_FAIL"); // se o registro falhar, envia mensagem de falha
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

        static string ObterStringConexao() // funcao que obtem a string de conexao para a base de dados
        {
            string db = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrivyChat.mdf"); // caminho do ficheiro da base de dados
            return @"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename=" + db + ";Integrated Security=True";
        }

        static byte[] GerarSalt() // funcao que gera um salt aleatorio de 8 bytes
        {
            byte[] salt = new byte[8];
            using (RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider())
            {
                rng.GetBytes(salt);
            }
            return salt;
        }

        static byte[] GerarHash(string senha, byte[] salt) // funcao que gera o hash da password usando o salt
        {
            using (Rfc2898DeriveBytes pbkdf2 = new Rfc2898DeriveBytes(senha, salt, 1000))
            {
                return pbkdf2.GetBytes(32);
            }
        }

        /*static bool SequenceEqual(byte[] a, byte[] b) // funcao que verifica se dois arrays de bytes sao iguais
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
                if (a[i] != b[i]) return false;
            return true;
        }*/

        static void InicializarBD() // funcao que inicializa a base de dados, criando se nao existir, e criando a tabela Users se nao existir
        {
            string db = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrivyChat.mdf"); // caminho do ficheiro da base de dados
            if (!File.Exists(db))
            {
                using (SqlConnection conn = new SqlConnection(@"Data Source=(LocalDB)\MSSQLLocalDB;Integrated Security=True"))
                {
                    conn.Open();
                    using (SqlCommand cmd = new SqlCommand("CREATE DATABASE PrivyChat ON PRIMARY (NAME = PrivyChat_Data, FILENAME = '" + db + "')", conn)) // cria a base de dados se nao existir
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
                Log("[Servidor] BD criado.");
            }
        }

        // Sistema de log unificado
        static void InicializarLog() // funcao que inicializa o sistema de log, criando o ficheiro de log se nao existir e escrevendo uma mensagem de inicializacao
        {
            try
            {
                // Cria o arquivo de log se não existir
                if (!File.Exists(caminhoLog))
                {
                    File.Create(caminhoLog).Dispose();
                }

                Log("=== SISTEMA DE LOG INICIALIZADO ===");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Não foi possível inicializar o sistema de log: {ex.Message}");
            }
        }

        static void Log(string mensagem) // funcao que escreve uma mensagem no log, tanto no console quanto no ficheiro de log
        {
            try
            {
                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                string linhaCompleta = $"[{timestamp}] {mensagem}";

                // Escreve no console
                Console.WriteLine(linhaCompleta);

                // Escreve no arquivo de log
                lock (lockLog) // bloqueia o objeto para garantir que apenas uma thread por vez possa escrever no log
                {
                    File.AppendAllText(caminhoLog, linhaCompleta + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERRO] Falha ao escrever no log: {ex.Message}");
            }
        }
    }
}