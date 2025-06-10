using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using EI.SI;
using System.Security.Cryptography;

namespace Servidor
{
    class Program
    {
        // Variáveis globais
        static TcpListener server; // Servidor TCP
        static List<TcpClient> clientes = new List<TcpClient>(); // Lista de clientes conectados
        static Dictionary<string, string> chavesPublicas = new Dictionary<string, string>(); // username → chave pública
        static object lockObj = new object(); // Objeto de bloqueio para acesso seguro à lista de clientes
        // Chaves AES para cada cliente
        static Dictionary<string, Aes> chavesAES = new Dictionary<string, Aes>(); // username → chave AES
        static RSACryptoServiceProvider rsaServidor = new RSACryptoServiceProvider(2048); // RSA do servidor

        static Dictionary<TcpClient, string> clientesUsernames = new Dictionary<TcpClient, string>();


        static void Main(string[] args)
        {
            int porta = 12345;
            server = new TcpListener(IPAddress.Any, porta);
            server.Start();
            Console.WriteLine($"[Servidor] A ouvir na porta {porta}...");

            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                Console.WriteLine("[Servidor] Cliente conectado.");
                Thread t = new Thread(TratarCliente);
                t.Start(client);
            }
        }

        static void TratarCliente(object obj)
        {
            TcpClient cliente = (TcpClient)obj;
            NetworkStream ns = cliente.GetStream();
            ProtocolSI protocolo = new ProtocolSI();

            string username = "";

            try
            {
                while (true)
                {
                    ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);

                    switch (protocolo.GetCmdType())
                    {
                        case ProtocolSICmdType.USER_OPTION_1:
                            // Enviar pedido de autenticação
                            byte[] msg = protocolo.Make(ProtocolSICmdType.DATA, "utilizador");
                            ns.Write(msg, 0, msg.Length);
                            break;

                        case ProtocolSICmdType.DATA:
                            if (string.IsNullOrEmpty(username))
                            {
                                username = protocolo.GetStringFromData();
                                lock (lockObj)
                                {
                                    clientes.Add(cliente);
                                    clientesUsernames[cliente] = username;
                                }
                                Console.WriteLine($"[Servidor] Utilizador identificado: {username}");

                                // Responde pedindo a chave pública do cliente
                                byte[] resposta = protocolo.Make(ProtocolSICmdType.DATA, "chave pública");
                                ns.Write(resposta, 0, resposta.Length);
                            }
                            else if (!chavesPublicas.ContainsKey(username))
                            {
                                // Recebe a chave pública do cliente
                                string chavePublicaBase64 = protocolo.GetStringFromData();
                                chavesPublicas[username] = chavePublicaBase64;
                                Console.WriteLine($"[Servidor] Chave pública recebida de {username}");

                                // Cria e envia a chave AES cifrada com RSA
                                EnviarChaveAES(username, ns, protocolo);
                            }
                            else
                            {
                                // Processa mensagens cifradas
                                string msgCifradaBase64 = protocolo.GetStringFromData();
                                string msgDecifrada = DecifrarMensagem(username, msgCifradaBase64);
                                Console.WriteLine($"[Mensagem de {username}]: {msgDecifrada}");

                                // Cifra novamente para enviar aos outros clientes
                                EnviarParaTodos($"[{username}]: {msgDecifrada}", cliente);
                            }
                            break;

                        case ProtocolSICmdType.EOF:
                            break;

                        case ProtocolSICmdType.EOT:
                            // Cliente desconectou
                            Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
                            EnviarParaTodos($"[{username}] Desconectou-se", cliente);
                            return;

                        default:
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
                cliente.Close();
                lock (lockObj)
                {
                    clientes.Remove(cliente);
                    clientesUsernames.Remove(cliente);
                }
                if (!string.IsNullOrEmpty(username))
                    EnviarParaTodos($"[{username}] Desconectou-se", cliente);

                Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
            }
        }

        static void EnviarParaTodos(string mensagem, TcpClient remetente)
        {
            ProtocolSI protocolo = new ProtocolSI();

            lock (lockObj)
            {
                foreach (TcpClient cli in clientes)
                {
                    if (cli != remetente)
                    {
                        try
                        {
                            NetworkStream ns = cli.GetStream();
                            // Obter username do destinatário (você precisará implementar isso)
                            string usernameDestino = ObterUsername(cli);

                            // Cifrar a mensagem com a chave AES do destinatário
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
        }

        static string ObterUsername(TcpClient cliente)
        {
            lock (lockObj)
            {
                if (clientesUsernames.ContainsKey(cliente))
                    return clientesUsernames[cliente];
                return null;
            }
        }

        static void EnviarChaveAES(string username, NetworkStream ns, ProtocolSI protocolo)
        {
            // Cria chave AES para este cliente
            Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();
            chavesAES[username] = aes;

            // Cifra a chave AES com a chave pública do cliente
            RSACryptoServiceProvider rsaCliente = new RSACryptoServiceProvider();
            rsaCliente.FromXmlString(chavesPublicas[username]);

            byte[] chaveAESCifrada = rsaCliente.Encrypt(aes.Key, false);
            byte[] ivCifrado = rsaCliente.Encrypt(aes.IV, false);

            // Envia a chave cifrada para o cliente
            string resposta = Convert.ToBase64String(chaveAESCifrada) + "|" + Convert.ToBase64String(ivCifrado);
            byte[] packet = protocolo.Make(ProtocolSICmdType.DATA, resposta);
            ns.Write(packet, 0, packet.Length);

            Console.WriteLine($"[Servidor] Chave AES enviada para {username}");
        }

        static string DecifrarMensagem(string username, string msgCifradaBase64)
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username];
            byte[] msgCifrada = Convert.FromBase64String(msgCifradaBase64);

            using (MemoryStream ms = new MemoryStream())
            {
                using (CryptoStream cs = new CryptoStream(ms, aes.CreateDecryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(msgCifrada, 0, msgCifrada.Length);
                    cs.FlushFinalBlock();
                    return Encoding.UTF8.GetString(ms.ToArray());
                }
            }
        }

        static string CifrarMensagem(string username, string mensagem)
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username];
            byte[] msgBytes = Encoding.UTF8.GetBytes(mensagem);

            using (MemoryStream ms = new MemoryStream())
            {
                using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
                {
                    cs.Write(msgBytes, 0, msgBytes.Length);
                    cs.FlushFinalBlock();
                    return Convert.ToBase64String(ms.ToArray());
                }
            }
        }


    }
}
