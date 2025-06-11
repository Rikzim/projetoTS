using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
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
        private TcpClient client;               // Cliente TCP para conexão com servidor
        private NetworkStream ns;               // Stream de rede para comunicação
        private ProtocolSI protocolo;           // Protocolo de comunicação 
        private Thread tReceber;                // Thread para receber mensagens

        // Objetos de criptografia
        private RSACryptoServiceProvider rsa;    // RSA para troca de chaves AES
        private RSACryptoServiceProvider rsaSign;// RSA para assinaturas digitais
        private Aes aesCliente;                 // AES para cifrar/decifrar mensagens
        private bool chaveAESRecebida = false;  // Flag indicando se a chave AES foi recebida

        // Dados do usuário
        private string nomeUtilizador;         // Nome do usuário atual

        // Controle de encerramento
        private volatile bool _isClosing = false;

        // Construtor do form
        public frmChat(string nomeUtilizador)
        {
            InitializeComponent();

            // Inicializa objetos
            protocolo = new ProtocolSI();
            rsa = new RSACryptoServiceProvider(2048);
            rsaSign = new RSACryptoServiceProvider(2048);

            this.nomeUtilizador = nomeUtilizador;
            txtUsername.Text = nomeUtilizador;

            CarregarImagemDoUtilizador();
        }

        // Evento de carregamento do form - inicia chat
        private void frmChat_Load(object sender, EventArgs e)
        {
            IniciarChat();
        }

        // Loop principal de recebimento de mensagens
        private void ReceberMensagens()
        {
            while (!_isClosing)
            {
                try
                {
                    // Lê dados do servidor
                    ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);

                    if (protocolo.GetCmdType() == ProtocolSICmdType.DATA)
                    {
                        string texto = protocolo.GetStringFromData();
                        if (!_isClosing)
                        {
                            BeginInvoke(new Action(() => ProcessarMensagemServidor(texto)));
                        }
                    }
                }
                catch (Exception)
                {
                    // Sair do loop se ocorrer erro de conexão
                    if (_isClosing) break;

                    try
                    {
                        if (!IsDisposed && !Disposing)
                        {
                            BeginInvoke(new Action(() =>
                            {
                                MessageBox.Show("A conexão com o servidor foi perdida.", "Erro");
                                Close();
                            }));
                        }
                    }
                    catch
                    {
                        // Ignorar erros de UI se o form já estiver fechado
                    }
                    break;
                }
            }
        }

        // Processa mensagens do servidor
        private void ProcessarMensagemServidor(string texto)
        {
            // Etapa de autenticação e troca de chaves
            if (texto.Contains("utilizador"))
            {
                EnviarMensagem(txtUsername.Text.Trim());
            }
            else if (texto.Contains("chave pública"))
            {
                EnviarMensagem(rsa.ToXmlString(false));
            }
            else if (texto.Contains("chave assinatura"))
            {
                EnviarMensagem(rsaSign.ToXmlString(false));
            }
            else if (texto.Contains("|") && !chaveAESRecebida)
            {
                ProcessarChaveAESRecebida(texto);
            }
            else
            {
                ProcessarMensagemRecebida(texto);
            }
        }

        // Envia mensagem para o servidor
        private void EnviarMensagem(string msg, ProtocolSICmdType tipo = ProtocolSICmdType.DATA)
        {
            byte[] dados = protocolo.Make(tipo, msg);
            ns.Write(dados, 0, dados.Length);
        }

        // Processa mensagens do chat
        private void ProcessarMensagemRecebida(string dados)
        {
            try
            {
                string[] partes = dados.Split(new[] { "||" }, StringSplitOptions.None);
                string mensagemDecifrada;
                string statusAssinatura = "";

                // Verifica formato da mensagem
                if (partes.Length == 3 && partes[2] == "VALID")
                {
                    mensagemDecifrada = DecifrarMensagem(partes[0]);
                    statusAssinatura = "✓";
                }
                else if (partes.Length == 2)
                {
                    mensagemDecifrada = DecifrarMensagem(partes[0]);
                    statusAssinatura = VerificarAssinatura(mensagemDecifrada, partes[1]) ? "✓" : "✗";
                }
                else
                {
                    mensagemDecifrada = DecifrarMensagem(dados);
                }

                Invoke(new Action(() => Log($"{statusAssinatura} {mensagemDecifrada}")));
            }
            catch (Exception ex)
            {
                Invoke(new Action(() => Log("Erro: " + ex.Message)));
            }
        }

        // Verifica assinatura digital
        private bool VerificarAssinatura(string mensagem, string assinaturaBase64)
        {
            try
            {
                byte[] dados = Encoding.UTF8.GetBytes(mensagem);
                byte[] assinatura = Convert.FromBase64String(assinaturaBase64);
                return rsaSign.VerifyData(dados, CryptoConfig.MapNameToOID("SHA256"), assinatura);
            }
            catch
            {
                return false;
            }
        }

        // Adiciona mensagem ao chat
        private void Log(string mensagem)
        {
            rtbChat.AppendText(mensagem + Environment.NewLine);
        }

        // Evento de clique no botão enviar
        private void enviar_Click(object sender, EventArgs e)
        {
            string texto = txtMensagem.Text.Trim();
            if (string.IsNullOrEmpty(texto)) return;

            if (!chaveAESRecebida)
            {
                MessageBox.Show("Aguarde a troca de chaves.", "Aviso");
                return;
            }

            try
            {
                string mensagemCifrada = CifrarMensagem(texto);
                string assinatura = AssinarMensagem(texto);
                string dadosCompletos = $"{mensagemCifrada}||{assinatura}";

                EnviarMensagem(dadosCompletos);
                txtMensagem.Clear();
                Log($"[Eu]: {texto} ✓");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro: " + ex.Message, "Erro");
            }
        }

        // Assina mensagem com RSA
        private string AssinarMensagem(string mensagem)
        {
            try
            {
                byte[] dados = Encoding.UTF8.GetBytes(mensagem);
                byte[] assinatura = rsaSign.SignData(dados, CryptoConfig.MapNameToOID("SHA256"));
                return Convert.ToBase64String(assinatura);
            }
            catch (Exception ex)
            {
                throw new Exception("Erro ao assinar: " + ex.Message);
            }
        }

        // Inicia conexão com servidor
        private void IniciarChat()
        {
            try
            {
                client = new TcpClient("127.0.0.1", 12345);
                ns = client.GetStream();

                tReceber = new Thread(ReceberMensagens) { IsBackground = true };
                tReceber.Start();

                EnviarMensagem(string.Empty, ProtocolSICmdType.USER_OPTION_1);
                Log("Conectado ao servidor.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro: " + ex.Message);
            }
        }

        // Carrega imagem do perfil do banco
        private void CarregarImagemDoUtilizador()
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PrivyChat.mdf");
            string connString = $@"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename={dbPath};Integrated Security=True";

            using (var conn = new SqlConnection(connString))
            {
                conn.Open();
                using (var cmd = new SqlCommand("SELECT ProfileImage FROM Users WHERE Username = @Username", conn))
                {
                    cmd.Parameters.AddWithValue("@Username", nomeUtilizador);
                    var result = cmd.ExecuteScalar();

                    if (result != DBNull.Value && result != null)
                    {
                        using (var ms = new MemoryStream((byte[])result))
                        {
                            pictureBox1.Image = Image.FromStream(ms);
                        }
                    }
                    else
                    {
                        pictureBox1.Image = Properties.Resources.pfp;
                    }
                }
            }
        }

        // Processa chave AES recebida do servidor
        private void ProcessarChaveAESRecebida(string chaveCifrada)
        {
            try
            {
                string[] partes = chaveCifrada.Split('|');
                if (partes.Length != 2) return;

                byte[] chaveAESBytes = rsa.Decrypt(Convert.FromBase64String(partes[0]), false);
                byte[] ivBytes = rsa.Decrypt(Convert.FromBase64String(partes[1]), false);

                aesCliente = Aes.Create();
                aesCliente.Key = chaveAESBytes;
                aesCliente.IV = ivBytes;
                chaveAESRecebida = true;

                //Invoke(new Action(() => MessageBox.Show("Chave AES configurada!", "Segurança")));
            }
            catch (Exception ex)
            {
                Invoke(new Action(() => MessageBox.Show("Erro: " + ex.Message)));
            }
        }

        // Cifra mensagem com AES
        private string CifrarMensagem(string mensagem)
        {
            if (!chaveAESRecebida || aesCliente == null)
                throw new Exception("Chave AES não pronta");

            using (var ms = new MemoryStream())
            using (var cs = new CryptoStream(ms, aesCliente.CreateEncryptor(), CryptoStreamMode.Write))
            {
                byte[] dados = Encoding.UTF8.GetBytes(mensagem);
                cs.Write(dados, 0, dados.Length);
                cs.FlushFinalBlock();
                return Convert.ToBase64String(ms.ToArray());
            }
        }

        // Decifra mensagem com AES
        private string DecifrarMensagem(string mensagemCifrada)
        {
            if (!chaveAESRecebida || aesCliente == null)
                return mensagemCifrada;

            try
            {
                using (var ms = new MemoryStream(Convert.FromBase64String(mensagemCifrada)))
                using (var cs = new CryptoStream(ms, aesCliente.CreateDecryptor(), CryptoStreamMode.Read))
                using (var sr = new StreamReader(cs))
                {
                    return sr.ReadToEnd();
                }
            }
            catch
            {
                return mensagemCifrada;
            }
        }

        // Finaliza conexão e limpa recursos
        private void sair_Click(object sender, EventArgs e)
        {
            try
            {
                _isClosing = true;  // Flag para indicar que estamos a fechar

                if (ns != null)
                {
                    try
                    {
                        EnviarMensagem(string.Empty, ProtocolSICmdType.EOT);
                    }
                    catch
                    {
                        // Ignoar erros ao enviar EOT, pois estamos a fechar
                    }
                    ns.Close();
                }

                // Nao esperar indefinidamente pela thread de recebimento
                if (tReceber != null && tReceber.IsAlive)
                {
                    tReceber.Join(1000); // Espera até 1 segundo para a thread terminar
                }

                aesCliente?.Dispose();
                rsaSign?.Dispose();
                rsa?.Dispose();
                client?.Close();
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao fechar: " + ex.Message);
            }
        }
    }
}