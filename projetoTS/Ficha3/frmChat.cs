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
        // Objetos de rede
        private TcpClient client;
        private NetworkStream ns;
        private ProtocolSI protocolo;
        private Thread tReceber;

        // Objetos de criptografia
        private RSACryptoServiceProvider rsaPublica;    // Para trocar a chave AES
        private RSACryptoServiceProvider rsaSign;       // Para assinar mensagens
        private Aes aesCliente;
        private bool chaveAESRecebida = false;

        // Dados do usuário
        private string nomeUtilizador;

        // Controle de encerramento
        private volatile bool _isClosing = false;

        public frmChat(string nomeUtilizador, TcpClient client, NetworkStream ns, ProtocolSI protocolo)
        {
            InitializeComponent();
            this.nomeUtilizador = nomeUtilizador;
            txtUsername.Text = nomeUtilizador;

            this.client = client;
            this.ns = ns;
            this.protocolo = protocolo;

            rsaPublica = new RSACryptoServiceProvider(2048);
            rsaSign = new RSACryptoServiceProvider(2048);

            try
            {
                IniciarChat();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao iniciar o chat: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        }

        private void IniciarChat()
        {
            // 1. Envia chave pública (AES)
            string chavePubXml = rsaPublica.ToXmlString(false);
            Send(ProtocolSICmdType.DATA, $"CHAVE_PUBLICA|{nomeUtilizador}|{chavePubXml}");

            // 2. Espera resposta do servidor ("chave assinatura")
            ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
            string resp = protocolo.GetStringFromData();
            if (!resp.Contains("chave assinatura"))
                throw new Exception("Resposta inesperada do servidor ao enviar chave pública");

            // 3. Envia chave de assinatura
            string chaveAssinaturaXml = rsaSign.ToXmlString(false);
            Send(ProtocolSICmdType.DATA, $"CHAVE_PUBLICA|{nomeUtilizador}|{chaveAssinaturaXml}");

            // 4. Espera resposta do servidor (chave AES)
            ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
            resp = protocolo.GetStringFromData();
            if (resp.Contains("|"))
            {
                string[] parts = resp.Split('|');
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

            tReceber = new Thread(ReceiveLoop);
            tReceber.IsBackground = true;
            tReceber.Start();
            Log("Conectado ao servidor.");
        }

        void Send(ProtocolSICmdType type, string msg)
        {
            byte[] pacote = protocolo.Make(type, msg);
            ns.Write(pacote, 0, pacote.Length);
        }

        private void ReceiveLoop()
        {
            try
            {
                while (!_isClosing)
                {
                    int bytesRead = ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                    if (bytesRead == 0) break;
                    if (protocolo.GetCmdType() == ProtocolSICmdType.DATA)
                    {
                        string msg = protocolo.GetStringFromData();
                        string show = ProcessReceivedMessage(msg);
                        if (!string.IsNullOrEmpty(show))
                        {
                            BeginInvoke(new Action(() =>
                            {
                                if (rtbChat != null)
                                    rtbChat.AppendText(show + Environment.NewLine);
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

        string ProcessReceivedMessage(string msg)
        {
            try
            {
                string[] parts = msg.Split(new[] { "||" }, StringSplitOptions.None);
                if (parts.Length == 2 && chaveAESRecebida)
                {
                    string decrypted = DecryptAES(parts[0]);
                    bool valid = VerifySignature(decrypted, parts[1]);
                    return (valid ? "✓ " : "✗ ") + decrypted;
                }
                return msg;
            }
            catch
            {
                return msg;
            }
        }

        private void enviar_Click(object sender, EventArgs e)
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
            string fullMsg = encrypted + "||" + signature;
            Send(ProtocolSICmdType.DATA, fullMsg);
            txtMensagem.Clear();
            Log($"[Eu]: {plain} ✓");
        }

        private void sair_Click(object sender, EventArgs e)
        {
            try
            {
                _isClosing = true;
                try
                {
                    Send(ProtocolSICmdType.EOT, "");
                }
                catch { 

                }
                if (tReceber != null && tReceber.IsAlive)
                {
                    tReceber.Join(1000);
                }
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

        // --- Crypto helpers ---
        string EncryptAES(string text)
        {
            using (MemoryStream ms = new MemoryStream())
            using (CryptoStream cs = new CryptoStream(ms, aesCliente.CreateEncryptor(), CryptoStreamMode.Write))
            {
                byte[] data = Encoding.UTF8.GetBytes(text);
                cs.Write(data, 0, data.Length);
                cs.FlushFinalBlock();
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        string DecryptAES(string enc)
        {
            using (MemoryStream ms = new MemoryStream(Convert.FromBase64String(enc)))
            using (CryptoStream cs = new CryptoStream(ms, aesCliente.CreateDecryptor(), CryptoStreamMode.Read))
            using (StreamReader sr = new StreamReader(cs))
            {
                return sr.ReadToEnd();
            }
        }

        string SignRSA(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] sig = rsaSign.SignData(data, CryptoConfig.MapNameToOID("SHA256"));
            return Convert.ToBase64String(sig);
        }

        bool VerifySignature(string text, string sig64)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            byte[] sig = Convert.FromBase64String(sig64);
            return rsaSign.VerifyData(data, CryptoConfig.MapNameToOID("SHA256"), sig);
        }

        private void Log(string mensagem)
        {
            if (rtbChat != null)
                rtbChat.AppendText(mensagem + Environment.NewLine);
        }
    }
}