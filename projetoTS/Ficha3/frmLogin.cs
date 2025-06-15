using System;
using System.Net.Sockets;
using System.Text;
using System.Windows.Forms;
using EI.SI;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.StartPanel;

namespace Ficha3
{
    public partial class frmLogin : Form
    {
        // criacao dos objetos de conexao e protocolo
        private TcpClient client;
        private NetworkStream ns;
        private ProtocolSI protocolo;

        public frmLogin()
        {
            InitializeComponent();
            protocolo = new ProtocolSI(); // inicializa o protocolo
        }

        private void ConectarServidor() // funcao para conectar ao servidor
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

        private bool VerifyLogin(string username, string password) // função para verificar o login
        {
            try
            {
                ConectarServidor();// conecta ao servidor

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

        private void btnEntrar_Click(object sender, EventArgs e) // função para entrar no chat após verificar o login
        {
            string password = txtPassword.Text;
            string username = txtUsername.Text;
            try
            {
                if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                {
                    throw new Exception("Preencha todos os campos!"); // lança uma exceção se os campos estiverem vazios
                }

                if (VerifyLogin(username, password))
                {
                    MessageBox.Show("Login Realizado", "Sucesso!", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    frmChat form1 = new frmChat(username, client, ns, protocolo); // cria o formulário de chat usando o mesmo cliente e protocolo
                    form1.Show();
                    this.Hide();
                }
                else
                {
                    throw new Exception("Username ou password inválido."); // lança uma exceção se o login falhar
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao entrar: " + ex.Message, "Erro !", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnRegistar_Click(object sender, EventArgs e) // função para abrir o formulário de registro
        {
            frmRegisto form3 = new frmRegisto();
            form3.FormClosed += (s, args) => this.Close();
            form3.ShowDialog();
        }
    }
}