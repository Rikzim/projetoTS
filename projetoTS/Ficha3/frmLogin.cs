using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Data.SqlClient;
using System.Drawing;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;

namespace Ficha3
{
    public partial class frmLogin: Form
    {
        private const int NUMBER_OF_ITERATIONS = 1000;

        public frmLogin()
        {
            InitializeComponent();
        }

        private void pictureBox1_Click(object sender, EventArgs e)
        {

        }

        private bool VerifyLogin(string username, string password)
        {
            SqlConnection conn = null;
            try
            {
                // Configurar ligação à Base de Dados
                conn = new SqlConnection();

                string dbFileName = "PrivyChat.mdf"; // ou "Data\\PrivyChat.mdf" se estiver em uma subpasta
                string dbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbFileName);

                conn.ConnectionString = String.Format($@"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename={dbFilePath};Integrated Security=True");

                return resposta == "LOGIN_OK";
            }
            catch (Exception e)
            {
                MessageBox.Show("An error occurred: " + e.Message);
                return false;
            }
        }
        private static byte[] GenerateSaltedHash(string plainText, byte[] salt)
        {
            Rfc2898DeriveBytes rfc2898 = new Rfc2898DeriveBytes(plainText, salt, NUMBER_OF_ITERATIONS);
            return rfc2898.GetBytes(32);
        }

        private void btnEntrar_Click(object sender, EventArgs e)
        {
            // Guarda a password e o salt gerados
            string password = txtPassword.Text;
            string username = txtUsername.Text;
            // Verifica se o utilizador existe na Base de Dados
            try
            {
                if (VerifyLogin(username, password))
                {
                    MessageBox.Show("Login Realizado!");
                    frmChat form1 = new frmChat(username, client, ns, protocolo);
                    form1.Show();
                    this.Hide(); // Esconde o formulário de login
                }
                else
                {
                    MessageBox.Show("Username ou password inválido.");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ocorreu um erro ao entrar: " + ex.Message);
            }
        }

        private void btnRegistar_Click(object sender, EventArgs e)
        {
            frmRegisto form3 = new frmRegisto();
            form3.ShowDialog();
        }
    }
}
