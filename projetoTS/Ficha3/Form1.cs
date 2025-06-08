using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using EI.SI;

namespace Ficha3
{   
    public partial class Form1: Form
    {
        TcpClient client;
        NetworkStream ns;
        ProtocolSI protocolo;
        RSACryptoServiceProvider rsa;
        Thread tReceber;


        public Form1()
        {
            InitializeComponent();

            protocolo = new ProtocolSI();
            rsa = new RSACryptoServiceProvider(2048); // Gera chave pública/privada

            Random random = new Random();
            // Define o nome de utilizador aleatório
            txtUsername.Text = "User" + random.Next(1000, 9999).ToString();
        }

        private void Ligar_Click(object sender, EventArgs e)
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
                    if (texto.Contains("nome de utilizador"))
                    {
                        string nome = txtUsername.Text.Trim();
                        byte[] nomeBytes = protocolo.Make(ProtocolSICmdType.DATA, nome);
                        ns.Write(nomeBytes, 0, nomeBytes.Length);
                    }

                    // Envia a chave pública
                    if (texto.Contains("chave pública"))
                    {
                        string chavePublicaBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(rsa.ToXmlString(false)));
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
    }
}
