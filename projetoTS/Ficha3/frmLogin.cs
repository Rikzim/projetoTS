using System;
using System.Windows.Forms;
using System.Net.Sockets;
using System.Text;
using EI.SI;

namespace Ficha3
{
    public partial class frmLogin : Form
    {
        private TcpClient client;
        private NetworkStream ns;
        private ProtocolSI protocolo;

        public frmLogin()
        {
            InitializeComponent();
            protocolo = new ProtocolSI();
        }

        private void ConectarServidor()
        {
            try
            {
                client = new TcpClient("127.0.0.1", 12345);
                ns = client.GetStream();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao conectar ao servidor: " + ex.Message);
            }
        }

        private bool VerifyLogin(string username, string password)
        {
            try
            {
                ConectarServidor();

                // Envia comando de login
                string loginData = $"LOGIN|{username}|{password}";
                byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, loginData);
                ns.Write(dados, 0, dados.Length);

                // Recebe resposta
                ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                string resposta = protocolo.GetStringFromData();

                return resposta == "LOGIN_OK";
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao verificar login: " + ex.Message);
                return false;
            }
        }

        private void btnEntrar_Click(object sender, EventArgs e)
        {
            string password = txtPassword.Text;
            string username = txtUsername.Text;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                MessageBox.Show("Preencha todos os campos!");
                return;
            }

            try
            {
                if (VerifyLogin(username, password))
                {
                    MessageBox.Show("Login Realizado!");
                    frmChat form1 = new frmChat(username, client, ns, protocolo);
                    form1.Show();
                    this.Hide();
                }
                else
                {
                    MessageBox.Show("Username ou password inválido.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao entrar: " + ex.Message);
            }
        }

        private void btnRegistar_Click(object sender, EventArgs e)
        {
            frmRegisto form3 = new frmRegisto();
            form3.ShowDialog();
        }
    }
}