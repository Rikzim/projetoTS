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
            // Configura e inicia o servidor na porta 12345
            server = new TcpListener(IPAddress.Any, 12345);
            server.Start();
            Console.WriteLine("[Servidor] A ouvir na porta 12345...");

            // Loop principal que aceita novas conexões
            while (true)
            {
                TcpClient client = server.AcceptTcpClient();
                Console.WriteLine("[Servidor] Cliente conectado.");
                Thread t = new Thread(TratarCliente);
                t.Start(client);
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
                while (true)
                {
                    // Lê dados do cliente
                    ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                    ProtocolSICmdType cmdType = protocolo.GetCmdType();

                    switch (cmdType)
                    {
                        case ProtocolSICmdType.USER_OPTION_1: // Pedido inicial de autenticação
                            byte[] msgAuth = protocolo.Make(ProtocolSICmdType.DATA, "utilizador");
                            ns.Write(msgAuth, 0, msgAuth.Length);
                            break;

                        case ProtocolSICmdType.DATA: // Processar dados recebidos
                            string dados = protocolo.GetStringFromData();
                            ProcessarDados(dados, ref username, cliente, ns, protocolo, ref chaveAESEnviada);
                            break;

                        case ProtocolSICmdType.EOT: // Cliente desconectou
                            NotificarDesconexao(username, cliente);
                            return;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro] {ex.Message}");
            }
            finally
            {
                FinalizarConexao(cliente, username);
            }
        }

        // Processa os dados recebidos do cliente baseado no estado da conexão
        static void ProcessarDados(string dados, ref string username, TcpClient cliente, NetworkStream ns, ProtocolSI protocolo, ref bool chaveAESEnviada)
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

        // Registra um novo cliente no servidor
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

        // Recebe e armazena a chave pública RSA para AES do cliente
        static void ReceberChavePublicaAES(string dados, string username, NetworkStream ns, ProtocolSI protocolo)
        {
            chavesPublicas[username] = dados;
            Console.WriteLine($"[Servidor] Chave pública (AES) recebida de {username}");
            byte[] msg = protocolo.Make(ProtocolSICmdType.DATA, "chave assinatura");
            ns.Write(msg, 0, msg.Length);
        }

        // Recebe a chave pública RSA para assinatura e envia a chave AES
        static void ReceberChaveAssinatura(string dados, string username, NetworkStream ns, ProtocolSI protocolo, ref bool chaveAESEnviada)
        {
            chavesAssinatura[username] = dados;
            Console.WriteLine($"[Servidor] Chave pública (assinatura) recebida de {username}");
            EnviarChaveAES(username, ns, protocolo);
            chaveAESEnviada = true;
        }

        // Processa mensagens assinadas do chat
        static void ProcessarMensagemComAssinatura(string remetente, string dadosRecebidos, TcpClient clienteRemetente)
        {
            try
            {
                // Divide a mensagem em duas partes: mensagem cifrada e assinatura
                string[] partes = dadosRecebidos.Split(new[] { "||" }, StringSplitOptions.None);
                if (partes.Length != 2)
                {
                    Console.WriteLine($"[ERRO] Formato de mensagem inválido de {remetente}");
                    return;
                }

                // Decifra a mensagem e verifica a assinatura
                string mensagemDecifrada = DecifrarMensagem(remetente, partes[0]);
                bool assinaturaValida = VerificarAssinatura(remetente, mensagemDecifrada, partes[1]);

                Console.WriteLine($"[{(assinaturaValida ? "✓" : "✗")} Mensagem de {remetente}]: {mensagemDecifrada}");

                // Se a assinatura for válida, encaminha para outros clientes
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

        // Verifica a assinatura digital de uma mensagem
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

                bool result = rsaVerify.VerifyData(dados, CryptoConfig.MapNameToOID("SHA256"), assinatura);
                rsaVerify.Dispose();
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Erro ao verificar assinatura] {ex.Message}");
                return false;
            }
        }

        // Envia uma mensagem para todos os clientes conectados exceto o remetente
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
                            if (string.IsNullOrEmpty(usernameDestino)) continue;

                            // Cifra a mensagem com a chave AES do destinatário
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

        // Obtém o username associado a um cliente
        static string ObterUsername(TcpClient cliente)
        {
            lock (lockObj)
            {
                return clientesUsernames.ContainsKey(cliente) ? clientesUsernames[cliente] : null;
            }
        }

        // Gera e envia uma nova chave AES para um cliente
        static void EnviarChaveAES(string username, NetworkStream ns, ProtocolSI protocolo)
        {
            // Cria uma nova chave AES
            Aes aes = Aes.Create();
            aes.KeySize = 256;
            aes.GenerateKey();
            aes.GenerateIV();
            chavesAES[username] = aes;

            // Cifra a chave AES com a chave pública do cliente
            RSACryptoServiceProvider rsaCliente = new RSACryptoServiceProvider();
            rsaCliente.FromXmlString(chavesPublicas[username]);

            string resposta = Convert.ToBase64String(rsaCliente.Encrypt(aes.Key, false)) +
                          "|" +
                          Convert.ToBase64String(rsaCliente.Encrypt(aes.IV, false));

            byte[] packet = protocolo.Make(ProtocolSICmdType.DATA, resposta);
            ns.Write(packet, 0, packet.Length);

            rsaCliente.Dispose();
            Console.WriteLine($"[Servidor] Chave AES enviada para {username}");
        }

        // Decifra uma mensagem usando a chave AES do usuário
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

        // Cifra uma mensagem usando a chave AES do usuário
        static string CifrarMensagem(string username, string mensagem)
        {
            if (!chavesAES.ContainsKey(username))
                throw new Exception("Chave AES não encontrada para o utilizador");

            Aes aes = chavesAES[username];
            byte[] msgBytes = Encoding.UTF8.GetBytes(mensagem);

            using (MemoryStream ms = new MemoryStream())
            using (CryptoStream cs = new CryptoStream(ms, aes.CreateEncryptor(), CryptoStreamMode.Write))
            {
                cs.Write(msgBytes, 0, msgBytes.Length);
                cs.FlushFinalBlock();
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        //Notifica outros clientes sobre a desconexão de um usuário
        static void NotificarDesconexao(string username, TcpClient cliente)
        {
            Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
            EnviarParaTodos($"[{username}] Desconectou-se", cliente, username);
        }

        // Finaliza a conexão de um cliente e limpa seus recursos
        static void FinalizarConexao(TcpClient cliente, string username)
        {
            cliente.Close();
            lock (lockObj)
            {
                clientes.Remove(cliente);
                clientesUsernames.Remove(cliente);
            }
            if (!string.IsNullOrEmpty(username))
            {
                EnviarParaTodos($"[{username}] Desconectou-se", cliente, username);
                Console.WriteLine($"[Servidor] Cliente {username} desconectado.");
            }
        }
    }
}