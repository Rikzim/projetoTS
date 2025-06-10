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
    public partial class frmChat : Form
    {
        TcpClient client;
        NetworkStream ns;
        ProtocolSI protocolo;
        RSACryptoServiceProvider rsa;
        RSACryptoServiceProvider rsaSign; // Para assinatura digital
        Thread tReceber;
        String nomeUtilizador;

        // Chave e IV fixos (para testes simples)
        private Aes aesCliente; // Será configurado quando recebermos do servidor
        private bool chaveAESRecebida = false;

        public frmChat(string nomeUtilizador)
        {
            InitializeComponent();

            protocolo = new ProtocolSI();
            rsa = new RSACryptoServiceProvider(2048); // Para troca de chaves
            rsaSign = new RSACryptoServiceProvider(2048); // Para assinatura digital

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
                        // Envia a chave pública (para troca de chaves AES)
                        else if (texto.Contains("chave pública"))
                        {
                            string chavePublica = rsa.ToXmlString(false);
                            byte[] chaveBytes = protocolo.Make(ProtocolSICmdType.DATA, chavePublica);
                            ns.Write(chaveBytes, 0, chaveBytes.Length);
                        }
                        // Envia a chave pública para assinatura digital
                        else if (texto.Contains("chave assinatura"))
                        {
                            string chaveAssinatura = rsaSign.ToXmlString(false);
                            byte[] chaveBytes = protocolo.Make(ProtocolSICmdType.DATA, chaveAssinatura);
                            ns.Write(chaveBytes, 0, chaveBytes.Length);
                        }
                        // Recebe a chave AES cifrada
                        else if (texto.Contains("|") && !chaveAESRecebida)
                        {
                            ProcessarChaveAESRecebida(texto);
                        }
                        // Mensagens normais do chat (agora com verificação de assinatura)
                        else
                        {
                            ProcessarMensagemRecebida(texto);
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

        private void ProcessarMensagemRecebida(string dadosRecebidos)
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
                    MessageBox.Show("Mensagem recebida: " + mensagemCifrada, "Informação", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    string mensagemDecifrada = DecifrarMensagem(mensagemCifrada);
                    MessageBox.Show("Mensagem decifrada: " + mensagemDecifrada, "Informação", MessageBoxButtons.OK, MessageBoxIcon.Information);

                    // Verifica a assinatura (assumindo que tens a chave pública do remetente)
                    bool assinaturaValida = VerificarAssinatura(mensagemDecifrada, assinaturaBase64);

                    string statusAssinatura = assinaturaValida ? "✓" : "✗";

                    Invoke(new MethodInvoker(() =>
                    {
                        Log($"{statusAssinatura} {mensagemDecifrada}");
                    }));
                }
                else
                {
                    // Mensagem sem assinatura (compatibilidade)
                    string mensagemDecifrada = DecifrarMensagem(dadosRecebidos);
                    Invoke(new MethodInvoker(() =>
                    {
                        Log(mensagemDecifrada);
                    }));
                }
            }
            catch (Exception ex)
            {
                Invoke(new MethodInvoker(() =>
                {
                    Log("Erro ao processar mensagem: " + ex.Message);
                }));
            }
        }

        private bool VerificarAssinatura(string mensagem, string assinaturaBase64)
        {
            try
            {
                byte[] dados = Encoding.UTF8.GetBytes(mensagem);
                byte[] assinatura = Convert.FromBase64String(assinaturaBase64);

                // Nota: Aqui precisarias da chave pública do remetente
                // Por simplicidade, estou usando a mesma chave (para teste)
                return rsaSign.VerifyData(dados, CryptoConfig.MapNameToOID("SHA256"), assinatura);
            }
            catch
            {
                return false;
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

                try
                {
                    // 1. Cifra a mensagem com AES
                    string mensagemCifrada = CifrarMensagem(texto);

                    // 2. Assina a mensagem original (antes de cifrar)
                    string assinatura = AssinarMensagem(texto);

                    // 3. Combina mensagem cifrada e assinatura
                    string dadosParaEnviar = mensagemCifrada + "||" + assinatura;

                    // 4. Envia tudo
                    byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, dadosParaEnviar);
                    ns.Write(dados, 0, dados.Length);

                    txtMensagem.Clear();
                    Log("[Eu]: " + texto + " ✓"); // Mostra a mensagem original com indicador de assinada
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Erro ao enviar mensagem: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

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
                throw new Exception("Erro ao assinar mensagem: " + ex.Message);
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
                throw new Exception("Chave AES não está pronta.");
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
                throw new Exception("Erro ao cifrar mensagem: " + ex.Message);
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

                if (rsaSign != null)
                {
                    rsaSign.Dispose();
                    rsaSign = null;
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