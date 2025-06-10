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
        private Aes aesCliente; // Será configurado quando recebermos do servidor
        private bool chaveAESRecebida = false;

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
                try
                {
                    ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                    if (protocolo.GetCmdType() == ProtocolSICmdType.DATA)
                    {
                        string texto = protocolo.GetStringFromData();

                        // Envia o username
                        if (texto.Contains("utilizador"))
                        {
                            string nome = txtUsername.Text.Trim();
                            byte[] nomeBytes = protocolo.Make(ProtocolSICmdType.DATA, nome);
                            ns.Write(nomeBytes, 0, nomeBytes.Length);
                        }
                        // Envia a chave pública
                        else if (texto.Contains("chave pública"))
                        {
                            string chavePublica = rsa.ToXmlString(false);
                            byte[] chaveBytes = protocolo.Make(ProtocolSICmdType.DATA, chavePublica);
                            ns.Write(chaveBytes, 0, chaveBytes.Length);
                        }
                        // Recebe a chave AES cifrada
                        else if (texto.Contains("|") && !chaveAESRecebida)
                        {
                            ProcessarChaveAESRecebida(texto);
                        }
                        // Mensagens normais do chat
                        else
                        {
                            string mensagemDecifrada = DecifrarMensagem(texto);
                            Invoke(new MethodInvoker(() =>
                            {
                                Log(mensagemDecifrada);
                            }));
                        }
                    }
                }
                catch (Exception ex)
                {
                    Invoke(new MethodInvoker(() =>
                    {
                        MessageBox.Show("Erro ao receber mensagens: " + ex.Message);
                    }));
                    break;
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
                if (!chaveAESRecebida)
                {
                    MessageBox.Show("Aguarde a troca de chaves ser completada.", "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string mensagemCifrada = CifrarMensagem(texto);
                byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, mensagemCifrada);
                ns.Write(dados, 0, dados.Length);
                txtMensagem.Clear();
                Log("[Eu]: " + texto); // Mostra a mensagem original (não cifrada) no chat
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

        private void ProcessarChaveAESRecebida(string chaveCifrada)
        {
            try
            {
                string[] partes = chaveCifrada.Split('|');
                if (partes.Length != 2) return;

                byte[] chaveAESCifrada = Convert.FromBase64String(partes[0]);
                byte[] ivCifrado = Convert.FromBase64String(partes[1]);

                // Decifra com a chave privada RSA
                byte[] chaveAESBytes = rsa.Decrypt(chaveAESCifrada, false);
                byte[] ivBytes = rsa.Decrypt(ivCifrado, false);

                // Configura o AES
                aesCliente = Aes.Create();
                aesCliente.Key = chaveAESBytes;
                aesCliente.IV = ivBytes;
                chaveAESRecebida = true;

                Invoke(new MethodInvoker(() =>
                {
                    MessageBox.Show("Chave AES configurada com sucesso!", "Segurança", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }));
            }
            catch (Exception ex)
            {
                Invoke(new MethodInvoker(() =>
                {
                    MessageBox.Show("Erro ao processar chave AES: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }));
            }
        }

        private string CifrarMensagem(string mensagem)
        {
            if (!chaveAESRecebida || aesCliente == null)
            {
                MessageBox.Show("Chave AES não está pronta. Aguarde a configuração.", "Aviso", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return mensagem;
            }

            try
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    using (CryptoStream cs = new CryptoStream(ms, aesCliente.CreateEncryptor(), CryptoStreamMode.Write))
                    {
                        byte[] textoBytes = Encoding.UTF8.GetBytes(mensagem);
                        cs.Write(textoBytes, 0, textoBytes.Length);
                        cs.FlushFinalBlock();
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao cifrar mensagem: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return mensagem;
            }
        }

        private string DecifrarMensagem(string mensagemCifrada)
        {
            if (!chaveAESRecebida || aesCliente == null)
                return mensagemCifrada;

            try
            {
                byte[] textoBytes = Convert.FromBase64String(mensagemCifrada);
                using (MemoryStream ms = new MemoryStream(textoBytes))
                {
                    using (CryptoStream cs = new CryptoStream(ms, aesCliente.CreateDecryptor(), CryptoStreamMode.Read))
                    {
                        using (StreamReader sr = new StreamReader(cs))
                        {
                            return sr.ReadToEnd();
                        }
                    }
                }
            }
            catch
            {
                return mensagemCifrada;
            }
        }

        private void sair_Click(object sender, EventArgs e)
        {
            try
            {
                tReceber.Abort();

                if (ns != null)
                {
                    byte[] eotPacket = protocolo.Make(ProtocolSICmdType.EOT);
                    ns.Write(eotPacket, 0, eotPacket.Length);
                    ns.Close();
                }

                // Limpa as chaves de criptografia
                if (aesCliente != null)
                {
                    aesCliente.Dispose();
                    aesCliente = null;
                }

                client.Close();
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao sair: " + ex.Message);
            }
        }
    }
}
