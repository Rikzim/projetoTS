using System;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using EI.SI;

namespace Ficha3
{
    public partial class frmChat : Form
    {
        // criacao dos objetos de conexao e protocolo
        private TcpClient client;
        private NetworkStream ns;
        private ProtocolSI protocolo;
        private Thread tReceber;


        // criacao dos objetos de criptografia
        private RSACryptoServiceProvider rsaPublica;// Chave publica para enviar ao servidor
        private RSACryptoServiceProvider rsaSign;// Para assinar mensagens
        private Aes aesCliente;// Chave AES recebida do servidor para desencriptar e encriptar mensagens
        private bool chaveAESRecebida = false;

        // dados do utilizador
        private string nomeUtilizador;

        // controle de encerramento
        private volatile bool _isClosing = false;

        public frmChat(string nomeUtilizador, TcpClient client, NetworkStream ns, ProtocolSI protocolo)
        {
            InitializeComponent();
            this.nomeUtilizador = nomeUtilizador;
            txtUsername.Text = nomeUtilizador;

            // Inicializa conexões e protocolo
            this.client = client;
            this.ns = ns;
            this.protocolo = protocolo;
            rsaPublica = new RSACryptoServiceProvider(2048);
            rsaSign = new RSACryptoServiceProvider(2048);
            try
            {
                IniciarChat(); // Inicia o processo de chat
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao iniciar o chat: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void IniciarChat() // funcao para iniciar o chat corretamente
        {
            // Envia chave publica para o servidor
            string chavePubXml = rsaPublica.ToXmlString(false);
            Send(ProtocolSICmdType.DATA, $"CHAVE_PUBLICA|{nomeUtilizador}|{chavePubXml}");

            // Espera resposta do servidor para confimar que a chave pública foi recebida
            ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
            string resp = protocolo.GetStringFromData();
            if (!resp.Contains("chave assinatura"))
                throw new Exception("Resposta inesperada do servidor ao enviar chave pública");

            // Envia chave de assinatura para o servidor
            string chaveAssinaturaXml = rsaSign.ToXmlString(false);
            Send(ProtocolSICmdType.DATA, $"CHAVE_PUBLICA|{nomeUtilizador}|{chaveAssinaturaXml}");

            // Espera resposta do servidor para guardar a chave AES recebida do servidor
            ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
            resp = protocolo.GetStringFromData();
            if (resp.Contains("|"))
            {
                string[] parts = resp.Split('|');// Divide a resposta em partes
                byte[] key = rsaPublica.Decrypt(Convert.FromBase64String(parts[0]), false);
                byte[] iv = rsaPublica.Decrypt(Convert.FromBase64String(parts[1]), false);
                aesCliente = Aes.Create();
                aesCliente.Key = key;
                aesCliente.IV = iv;
                chaveAESRecebida = true;
            }
            else
            {
                throw new Exception("Chave AES não recebida do servidor.");
            }
            // Inicia a thread para receber mensagens
            tReceber = new Thread(ReceiveLoop);
            tReceber.IsBackground = true;
            tReceber.Start();
            Log("Conectado ao servidor.");
        }

        void Send(ProtocolSICmdType type, string msg) // funcao para enviar mensagens ao servidor
        {
            byte[] pacote = protocolo.Make(type, msg);
            ns.Write(pacote, 0, pacote.Length);
        }

        private void ReceiveLoop()// funcao para receber mensagens do servidor
        {
            try
            {
                while (!_isClosing) // Continua recebendo ate o formulario fechar
                {
                    int bytesRead = ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                    if (bytesRead == 0) break;
                    if (protocolo.GetCmdType() == ProtocolSICmdType.DATA)
                    {
                        string msg = protocolo.GetStringFromData();
                        string show = ProcessReceivedMessage(msg);// processa a mensagem recebida, verifica assinatura e descriptografa
                        if (!string.IsNullOrEmpty(show))
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (rtbChat != null)
                                    rtbChat.AppendText(show + Environment.NewLine);// Adiciona a mensagem ao chat
                            }));
                        }
                    }
                }
            }
            catch
            {
                if (!_isClosing)
                {
                    BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show("Desconectado do servidor.");
                        Close();
                    }));
                }
            }
        }
        string ProcessReceivedMessage(string msg)// funcao para verificar a assinatura e descriptografar a mensagem recebida
        {
            try
            {
                string[] parts = msg.Split(new[] { "||" }, StringSplitOptions.None);// divide a mensagem em partes usando || como separador
                if (parts.Length == 2 && chaveAESRecebida)
                {
                    string decrypted = DecryptAES(parts[0]);
                    bool valid = VerifySignature(decrypted, parts[1]);
                    return (valid ? "V" : "X") + decrypted;
                }
                return msg;
            }
            catch
            {
                return msg;
            }
        }
        private void enviar_Click(object sender, EventArgs e)// funcao para enviar mensagens
        {
            if (!chaveAESRecebida)
            {
                MessageBox.Show("Chave AES ainda não recebida.");
                return;
            }
            string plain = txtMensagem.Text.Trim();
            if (plain == "") return;

            string encrypted = EncryptAES(plain);
            string signature = SignRSA(plain);
            string fullMsg = encrypted + "||" + signature; // manda a mensagem separada em 2 partes, a msg cifrada e a assinatura
            Send(ProtocolSICmdType.DATA, fullMsg);
            txtMensagem.Clear();
            Log($"[Eu]: {plain}");
        }
        private void sair_Click(object sender, EventArgs e)// funcao para fechar o chat
        {
            try
            {
                _isClosing = true;
                try
                {
                    Send(ProtocolSICmdType.EOT, "");// manda o protoloco de encerramento para o servidor
                }
                catch
                {
                    // Se falhar ao enviar o EOT, não faz nada

                }
                if (tReceber != null && tReceber.IsAlive)// espera a thread de receber mensagens terminar
                {
                    tReceber.Join(1000);
                }
                // Libera recursos
                aesCliente?.Dispose();
                rsaSign?.Dispose();
                rsaPublica?.Dispose();
                ns?.Close();
                client?.Close();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao fechar: " + ex.Message);
            }
        }
        // funcoes para criptografia e assinatura
        string EncryptAES(string text)// funcao para encriptar mensagens usando a chave aes enviada pelo servidor
        {
            using (MemoryStream ms = new MemoryStream())
            using (CryptoStream cs = new CryptoStream(ms, aesCliente.CreateEncryptor(), CryptoStreamMode.Write))
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                cs.Write(data, 0, data.Length);
                cs.FlushFinalBlock(); // garante que todos os dados sejam escritos corretamente
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        string DecryptAES(string msgCifrada) // funcao para desencriptar mensagens usando a chave aes enviada pelo servidor
        {
            using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(msgCifrada)))
            using (CryptoStream cs = new CryptoStream(ms, aesCliente.CreateDecryptor(), CryptoStreamMode.Read))
            using (StreamReader sr = new StreamReader(cs))
            {
                return sr.ReadToEnd();// le todos os dados desencriptados e retorna como string
            }
        }
        string SignRSA(string text)// funcao para assinar mensagens usando a chave de assinatura
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] sig = rsaSign.SignData(data, CryptoConfig.MapNameToOID("SHA256"));
            return Convert.ToBase64String(sig);
        }

        bool VerifySignature(string text, string sig64) // funcao para verificar a assinatura das mensagens
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] sig = Convert.FromBase64String(sig64);
            return rsaSign.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), sig);
        }
        private void Log(string mensagem) // funcao para adicionar mensagens ao chat
        {
            if (rtbChat != null)
            {
                rtbChat.AppendText(mensagem + Environment.NewLine);
            }
        }
    }
}