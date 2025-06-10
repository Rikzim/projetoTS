using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EI.SI;

namespace Ficha3
{
    public partial class frmChat: Form
    {
        TcpClient client;
        NetworkStream ns;
        ProtocolSI protocolo;
        RSACryptoServiceProvider rsa;
        Thread tReceber;
        String nomeUtilizador;

        // Chave e IV fixos (para testes simples)
        byte[] chaveAES = Encoding.UTF8.GetBytes("minha_chave_segura_256bits!!"); // 24 ou 32 bytes
        byte[] ivAES = Encoding.UTF8.GetBytes("iv_simples_16byte"); // 16 bytes

        public frmChat(string nomeUtilizador)
        {
            InitializeComponent();

            protocolo = new ProtocolSI();
            rsa = new RSACryptoServiceProvider(2048); // Gera chave pública/privada
            // Recebe o nome de utilizador do formulário de login
            this.nomeUtilizador = nomeUtilizador;
            txtUsername.Text = nomeUtilizador; // Preenche o campo de username

            CarregarImagemDoUtilizador(); // Carrega a imagem do utilizador
        }

        private void frmChat_Load(object sender, EventArgs e)
        {
            InciarChat(); // Inicia a conexão com o servidor quando o formulário é carregado
        }


        private void ReceberMensagens()
        {
            while (true)
            {
                ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                if (protocolo.GetCmdType() == ProtocolSICmdType.DATA)
                {
                    string texto = protocolo.GetStringFromData();

                    // Resposta do servidor a cada fase
                    Invoke(new MethodInvoker(() =>
                    {
                        Log(texto);
                    }));

                    // Envia o username
                    if (texto.Contains("utilizador"))
                    {
                        string nome = txtUsername.Text.Trim();
                        byte[] nomeBytes = protocolo.Make(ProtocolSICmdType.DATA, nome);
                        ns.Write(nomeBytes, 0, nomeBytes.Length);
                    }

                    // Envia a chave pública
                    if (texto.Contains("chave pública"))
                    {
                        string chavePublicaBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ToXmlString(false)));
                        MessageBox.Show("Chave pública enviada: " + chavePublicaBase64, "Chave Pública", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        byte[] chaveBytes = protocolo.Make(ProtocolSICmdType.DATA, chavePublicaBase64);
                        ns.Write(chaveBytes, 0, chaveBytes.Length);
                    }
                }
            }
        }

        private void Log(string mensagem)
        {
            rtbChat.AppendText(mensagem + Environment.NewLine);
        }

        private void enviar_Click(object sender, EventArgs e)
        {
            string texto = txtMensagem.Text.Trim();

            if (!string.IsNullOrEmpty(texto))
            {
                byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, texto);
                ns.Write(dados, 0, dados.Length);
                txtMensagem.Clear();
                Log("[Eu]: " + texto);
            }
        }

        private void InciarChat()
        {
            try
            {
                client = new TcpClient("127.0.0.1", 12345); // IP e porta do servidor
                ns = client.GetStream();

                tReceber = new Thread(ReceberMensagens);
                tReceber.IsBackground = true;
                tReceber.Start();

                // Envia USER_OPTION_1 (começa o protocolo)
                byte[] iniciar = protocolo.Make(ProtocolSICmdType.USER_OPTION_1);
                ns.Write(iniciar, 0, iniciar.Length);
                Log("Ligado ao servidor.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro: " + ex.Message);
            }
        }

        private void CarregarImagemDoUtilizador()
        {
            string dbFileName = "PrivyChat.mdf"; // ou "Data\\PrivyChat.mdf" se estiver em uma subpasta
            string dbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbFileName);

            string connectionString = String.Format($@"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename={dbFilePath};Integrated Security=True"); 

            using (SqlConnection conn = new SqlConnection(connectionString))
            {
                conn.Open();
                string sql = "SELECT ProfileImage FROM Users WHERE Username = @Username";


                using (SqlCommand cmd = new SqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@Username", nomeUtilizador);
                    var result = cmd.ExecuteScalar();

                    if (result != DBNull.Value && result != null)
                    {
                        byte[] imageBytes = (byte[])result;
                        using (MemoryStream ms = new MemoryStream(imageBytes))
                        {
                            pictureBox1.Image = Image.FromStream(ms);
                        }
                    }
                    else
                    {
                        // Se não tiver imagem, podes mostrar uma imagem padrão
                        pictureBox1.Image = Properties.Resources.pfp; // se tiveres uma imagem embutida
                    }
                }
            }
        }

        private void sair_Click(object sender, EventArgs e)
        {
            try
            {
                tReceber.Abort(); // Interrompe a thread de recebimento de mensagens

                if (ns != null)
                {
                    byte[] eotPacket = protocolo.Make(ProtocolSICmdType.EOT);
                    ns.Write(eotPacket, 0, eotPacket.Length); // Envia EOT para o servidor
                    ns.Close(); // Fecha o stream
                }
                client.Close(); // Fecha a conexão com o servidor
                this.Close(); // Fecha o formulário
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao sair: " + ex.Message);
            }
        }
    }
}
