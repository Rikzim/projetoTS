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
        static Dictionary<string, string> chavesPublicas = new Dictionary<string, string>(); // username → chave pública RSA (para AES)
        static Dictionary<string, string> chavesAssinatura = new Dictionary<string, string>(); // username → chave pública RSA (para assinatura)
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
            bool chaveAESEnviada = false;

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
                            string dados = protocolo.GetStringFromData();

                            if (string.IsNullOrEmpty(username))
                            {
                                // Primeiro dados recebidos: username
                                username = dados;
                                lock (lockObj)
                                {
                                    clientes.Add(cliente);
                                    clientesUsernames[cliente] = username;
                                }
                                Console.WriteLine($"[Servidor] Utilizador identificado: {username}");

                                // Responde pedindo a chave pública do cliente (para AES)
                                byte[] resposta = protocolo.Make(ProtocolSICmdType.DATA, "chave pública");
                                ns.Write(resposta, 0, resposta.Length);
                            }
                            else if (!chavesPublicas.ContainsKey(username))
                            {
                                // Segunda: Recebe a chave pública do cliente (para AES)
                                chavesPublicas[username] = dados;
                                Console.WriteLine($"[Servidor] Chave pública (AES) recebida de {username}");

                                // Pede a chave pública para assinatura
                                byte[] resposta = protocolo.Make(ProtocolSICmdType.DATA, "chave assinatura");
                                ns.Write(resposta, 0, resposta.Length);
                            }
                            else if (!chavesAssinatura.ContainsKey(username))
                            {
                                // Terceira: Recebe a chave pública para assinatura
                                chavesAssinatura[username] = dados;
                                Console.WriteLine($"[Servidor] Chave pública (assinatura) recebida de {username}");

                                // Agora sim, envia a chave AES cifrada
                                EnviarChaveAES(username, ns, protocolo);
                                chaveAESEnviada = true;
                            }
                            else if (chaveAESEnviada)
                            {
                                // Quarta em diante: Processa mensagens com assinatura
                                ProcessarMensagemComAssinatura(username, dados, cliente);
                            }
                            break;

                        case ProtocolSICmdType.EOF:
                            break;

                        case ProtocolSICmdType.EOT:
                            // Cliente desconectou
                            Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
                            EnviarParaTodos($"[{username}] Desconectou-se", cliente, username);
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
                    EnviarParaTodos($"[{username}] Desconectou-se", cliente, username);

                Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
            }
        }

        static void ProcessarMensagemComAssinatura(string remetente, string dadosRecebidos, TcpClient clienteRemetente)
        {
            try
            {
                // Formato esperado: mensagemCifrada||assinatura
                string[] partes = dadosRecebidos.Split(new string[] { "||" }, StringSplitOptions.None);

                if (partes.Length == 2)
                {
                    string mensagemCifrada = partes[0];
                    string assinaturaBase64 = partes[1];

                    // Decifra a mensagem
                    string mensagemDecifrada = DecifrarMensagem(remetente, mensagemCifrada);

                    // Verifica a assinatura
                    bool assinaturaValida = VerificarAssinatura(remetente, mensagemDecifrada, assinaturaBase64);

                    string statusAssinatura = assinaturaValida ? "✓" : "✗";
                    Console.WriteLine($"[{statusAssinatura} Mensagem de {remetente}]: {mensagemDecifrada}");

                    if (assinaturaValida)
                    {
                        // Reenvia a mensagem para todos os outros clientes (com a assinatura original)
                        string mensagemCompleta = $"[{remetente}]: {mensagemDecifrada}";
                        EnviarParaTodosComAssinatura(mensagemCompleta, clienteRemetente, remetente, assinaturaBase64);
                    }
                    else
                    {
                        Console.WriteLine($"[AVISO] Assinatura inválida de {remetente}. Mensagem não foi retransmitida.");
                        // Opcionalmente, podes enviar uma notificação de erro para o remetente
                    }
                }
                else
                {
                    Console.WriteLine($"[ERRO] Formato de mensagem inválido de {remetente}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro ao processar mensagem com assinatura] {ex.Message}");
            }
        }

        static bool VerificarAssinatura(string username, string mensagem, string assinaturaBase64)
        {
            try
            {
                if (!chavesAssinatura.ContainsKey(username))
                {
                    Console.WriteLine($"[ERRO] Chave de assinatura não encontrada para {username}");
                    return false;
                }

                RSACryptoServiceProvider rsaVerify = new RSACryptoServiceProvider();
                rsaVerify.FromXmlString(chavesAssinatura[username]);

                byte[] dados = Encoding.UTF8.GetBytes(mensagem);
                byte[] assinatura = Convert.FromBase64String(assinaturaBase64);

                return rsaVerify.VerifyData(dados, CryptoConfig.MapNameToOID("SHA256"), assinatura);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro ao verificar assinatura] {ex.Message}");
                return false;
            }
        }

        static void EnviarParaTodosComAssinatura(string mensagem, TcpClient remetente, string usernameRemetente, string assinaturaOriginal)
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
                            string usernameDestino = ObterUsername(cli);

                            if (usernameDestino != null)
                            {
                                // Cifrar a mensagem com a chave AES do destinatário
                                string msgCifrada = CifrarMensagem(usernameDestino, mensagem);

                                // Combina mensagem cifrada + assinatura + status de verificação
                                string dadosParaEnviar = msgCifrada + "||" + assinaturaOriginal + "||VALID";

                                byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, dadosParaEnviar);
                                ns.Write(dados, 0, dados.Length);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[Erro ao enviar mensagem] {ex.Message}");
                        }
                    }
                }
            }
        }

        static void EnviarParaTodos(string mensagem, TcpClient remetente, string usernameRemetente)
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
                            string usernameDestino = ObterUsername(cli);

                            if (usernameDestino != null)
                            {
                                // Cifrar a mensagem com a chave AES do destinatário
                                string msgCifrada = CifrarMensagem(usernameDestino, mensagem);
                                byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, msgCifrada);

                                ns.Write(dados, 0, dados.Length);
                            }
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