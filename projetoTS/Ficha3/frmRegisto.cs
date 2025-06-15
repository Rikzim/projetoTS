using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using System.IO;
using System.Data.SqlClient;
using System.Security.Cryptography;

namespace Ficha3
{
    public partial class frmRegisto: Form
    {
        // criacao dos objetos de conexao e protocolo
        private TcpClient client;
        private NetworkStream ns;
        private ProtocolSI protocolo;

        public frmRegisto()
        {
            InitializeComponent();
        }

        private void ConectarServidor()// funcao para conectar ao servidor
        {
            try
            {
                using (OpenFileDialog ofd = new OpenFileDialog())
                {
                    ofd.Filter = "Imagens|*.jpg;*.jpeg;*.png;*.bmp";
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        pbImagem.Image = Image.FromFile(ofd.FileName);
                        profileImageBytes = File.ReadAllBytes(ofd.FileName);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erro ao carregar imagem: " + ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

        }

        private void btnRegistar_Click(object sender, EventArgs e)
        {
            string password = txtPassword.Text;
            byte[] salt = GenerateSalt(SALTSIZE);
            byte[] saltedPasswordHash = GenerateSaltedHash(password, salt);
            try
            {
                Register(txtUsername.Text, txtPassword.Text);
                MessageBox.Show("Utilizador registrado com sucesso!");
                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Register(string username, string password) // função para registrar o utilizador
        {

            SqlConnection conn = null;
            try
            {
                // Configurar ligação à Base de Dados
                conn = new SqlConnection();
                string dbFileName = "PrivyChat.mdf"; // ou "Data\\PrivyChat.mdf" se estiver em uma subpasta
                string dbFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, dbFileName);

                conn.ConnectionString = String.Format($@"Data Source=(LocalDB)\MSSQLLocalDB;AttachDbFilename={dbFilePath};Integrated Security=True");

                // envia a mensagem de registro
                string registerData = $"REGISTER|{username}|{password}";
                byte[] dados = protocolo.Make(ProtocolSICmdType.DATA, registerData);
                ns.Write(dados, 0, dados.Length);

                // recebe a resposta
                ns.Read(protocolo.Buffer, 0, protocolo.Buffer.Length);
                string resposta = protocolo.GetStringFromData();

                // Declaração do comando SQL
                String sql = "INSERT INTO Users (Username, SaltedPasswordHash, Salt, ProfileImage) VALUES (@username,@saltedPasswordHash,@salt,@profileImage)";

                // Prepara comando SQL para ser executado na Base de Dados
                SqlCommand cmd = new SqlCommand(sql, conn);

                // Introduzir valores aos parâmentros registados no comando SQL
                cmd.Parameters.Add(paramUsername);
                cmd.Parameters.Add(paramPassHash);
                cmd.Parameters.Add(paramSalt);
                cmd.Parameters.Add(paramProfileImage);

                // Executar comando SQL
                int lines = cmd.ExecuteNonQuery();

                // Fechar ligação
                conn.Close();
                if (lines == 0)
                {
                    // Se forem devolvidas 0 linhas alteradas então o não foi executado com sucesso
                    throw new Exception("Error while inserting an user");
                }
            }
            catch (Exception e)
            {
                throw new Exception("Error while inserting an user:" + e.Message);
            }
        }

        private static byte[] GenerateSalt(int size)
        {
            //Generate a cryptographic random number.
            RNGCryptoServiceProvider rng = new RNGCryptoServiceProvider();
            byte[] buff = new byte[size];
            rng.GetBytes(buff);
            return buff;
        }

        private static byte[] GenerateSaltedHash(string plainText, byte[] salt)
        {
            Rfc2898DeriveBytes rfc2898 = new Rfc2898DeriveBytes(plainText, salt, NUMBER_OF_ITERATIONS);
            return rfc2898.GetBytes(32);
        }
    }
}
