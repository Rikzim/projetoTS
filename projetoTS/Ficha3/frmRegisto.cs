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
        public frmRegisto()
        {
            InitializeComponent();
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
                using (TcpClient regClient = new TcpClient("127.0.0.1", 12345)) // utilizando uma nova conexao para o registo
                using (NetworkStream regNs = regClient.GetStream()) // criando o NetworkStream para enviar e receber dados
                {
                    ProtocolSI regProtocolo = new ProtocolSI();

                    string registerData = $"REGISTER|{username}|{password}";
                    byte[] dados = regProtocolo.Make(ProtocolSICmdType.DATA, registerData);
                    regNs.Write(dados, 0, dados.Length);

                    regNs.Read(regProtocolo.Buffer, 0, regProtocolo.Buffer.Length);
                    string resposta = regProtocolo.GetStringFromData();

                    if (resposta != "REGISTER_OK")
                        throw new Exception("Erro ao registrar usuário: " + resposta);
                }
            }
            catch (Exception ex)
            {
                throw new Exception("Erro ao registrar usuário: " + ex.Message);
            }
        }
    }
}