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
                                Console.WriteLine($"[Servidor] Utilizador identificado: {username}");

                                // Responde pedindo a chave pública
                                byte[] resposta = protocolo.Make(ProtocolSICmdType.DATA, "chave pública");
                                ns.Write(resposta, 0, resposta.Length);
                            }
                            else if (!chavesPublicas.ContainsKey(username))
                            {
                                string chavePublicaBase64 = protocolo.GetStringFromData();
                                chavesPublicas[username] = chavePublicaBase64;
                                Console.WriteLine($"[Servidor] Chave pública recebida de {username}");
                                // Avisar cliente que está autenticado
                                byte[] ok = protocolo.Make(ProtocolSICmdType.DATA, "Autenticado com sucesso!");
                                ns.Write(ok, 0, ok.Length);
                                

                                // Adicionar à lista de clientes
                                lock (lockObj)
                                    clientes.Add(cliente);
                            }
                            else
                            {
                                string msgChat = protocolo.GetStringFromData();
                                Console.WriteLine($"[Mensagem de {username}]: {msgChat}");

                                // Enviar mensagem a todos os outros clientes
                                EnviarParaTodos($"[{username}]: {msgChat}", cliente);
                            }
                            break;

                        case ProtocolSICmdType.EOF:
                            break;

                        case ProtocolSICmdType.EOT:
                            // Cliente desconectou
                            Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
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
                    clientes.Remove(cliente);
                Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
            }
        }

        static void EnviarParaTodos(string mensagem, TcpClient remetente)
        {
            ProtocolSI protocolo = new ProtocolSI();
            byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, mensagem);

            lock (lockObj)
            {
                foreach (TcpClient cli in clientes)
                {
                    if (cli != remetente)
                    {
                        NetworkStream ns = cli.GetStream();
                        ns.Write(dados, 0, dados.Length);
                    }
                }
            }
        }


    }
}
