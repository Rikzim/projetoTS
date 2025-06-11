using System;
using System.Windows.Forms;
using System.Drawing;
using System.Net.Sockets;
using System.Text;
using System.IO;
using EI.SI;

namespace Ficha3
{
    public partial class frmRegisto : Form
    {
        private TcpClient client;
        private NetworkStream ns;
        private ProtocolSI protocolo;

        public frmRegisto()
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
        private void btnRegistar_Click(object sender, EventArgs e)
        {
            if (string.IsNullOrEmpty(txtUsername.Text) || string.IsNullOrEmpty(txtPassword.Text))
            {
                MessageBox.Show("Preencha todos os campos obrigatórios.");
                return;
            }
            try
            {
                Register(txtUsername.Text, txtPassword.Text);
                MessageBox.Show("Usuário registrado com sucesso!");
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao registrar: " + ex.Message);
            }
        }

        private void Register(string username, string password)
        {
            try
            {
                ConectarServidor();


                // Envia comando de registro
                string registerData = $"REGISTER|{username}|{password}";
                byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, registerData);
                ns.Write(dados, 0, dados.Length);

                // Recebe resposta
                ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                string resposta = protocolo.GetStringFromData();

                if (resposta != "REGISTER_OK")
                    throw new Exception("Erro ao registrar usuário");

                client.Close();
            }
            catch (Exception ex)
            {
                throw new Exception("Erro ao registrar usuário: " + ex.Message);
            }
        }
    }
}