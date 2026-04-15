using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatClient
{
    class Program
    {
        static void Main(string[] args)
        {
            var client = new ChatClient();
            client.Start().Wait();
        }
    }

    public class ChatClient
    {
        private TcpClient tcpClient;
        private NetworkStream stream;
        private string userName;
        private bool isConnected = false;
        private Thread receiveThread;

        private bool IsValidIPv4(string ip)
        {
            if (!IPAddress.TryParse(ip, out IPAddress address))
                return false;
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        private bool IsValidPort(int port)
        {
            return port >= 1 && port <= 65535;
        }

        private bool IsNumericString(string str)
        {
            return int.TryParse(str, out _);
        }

        public async Task Start()
        {
            Console.WriteLine("=== TCP ЧАТ КЛИЕНТ ===");

            // === ВВОД ИМЕНИ ===
            while (true)
            {
                Console.Write("Введите ваше имя (не пустое, не более 20 символов): ");
                userName = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(userName))
                {
                    Console.WriteLine("Ошибка: Имя не может быть пустым!");
                    continue;
                }

                if (userName.Length > 20)
                {
                    Console.WriteLine("Ошибка: Имя не может быть длиннее 20 символов!");
                    continue;
                }

                break;
            }

            // === ВВОД IP СЕРВЕРА ===
            string serverIp;
            while (true)
            {
                Console.Write("Введите IP адрес сервера (например, 127.0.0.x): ");
                serverIp = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(serverIp))
                {
                    Console.WriteLine("Ошибка: IP адрес не может быть пустым!");
                    continue;
                }

                if (!IsValidIPv4(serverIp))
                {
                    Console.WriteLine($"Ошибка: '{serverIp}' - не корректный IPv4 адрес!");
                    continue;
                }

                break;
            }

            // === ВВОД ПОРТА СЕРВЕРА ===
            int tcpPort;
            while (true)
            {
                Console.Write("Введите TCP порт сервера (1-65535): ");
                string portInput = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(portInput))
                {
                    Console.WriteLine("Ошибка: Порт не может быть пустым!");
                    continue;
                }

                if (!IsNumericString(portInput))
                {
                    Console.WriteLine($"Ошибка: '{portInput}' - не число!");
                    continue;
                }

                tcpPort = int.Parse(portInput);

                if (!IsValidPort(tcpPort))
                {
                    Console.WriteLine($"Ошибка: Порт {tcpPort} недопустим!");
                    continue;
                }

                break;
            }

            // === ВВОД ЛОКАЛЬНОГО IP КЛИЕНТА ===
            string localIp;
            while (true)
            {
                Console.Write("Введите ваш локальный IP адрес (например, 127.0.0.1): ");
                localIp = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(localIp))
                {
                    Console.WriteLine("Ошибка: Локальный IP не может быть пустым!");
                    continue;
                }

                if (!IsValidIPv4(localIp))
                {
                    Console.WriteLine($"Ошибка: '{localIp}' - не корректный IPv4 адрес!");
                    continue;
                }

                break;
            }

            // === ВВОД ЛОКАЛЬНОГО ПОРТА КЛИЕНТА ===
            int localPort;
            while (true)
            {
                Console.Write("Введите ваш локальный TCP порт (1-65535, уникальный): ");
                string portInput = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(portInput))
                {
                    Console.WriteLine("Ошибка: Порт не может быть пустым!");
                    continue;
                }

                if (!IsNumericString(portInput))
                {
                    Console.WriteLine($"Ошибка: '{portInput}' - не число!");
                    continue;
                }

                localPort = int.Parse(portInput);

                if (!IsValidPort(localPort))
                {
                    Console.WriteLine($"Ошибка: Порт {localPort} недопустим!");
                    continue;
                }

                break;
            }

            // === ПРОВЕРКА 1: IP клиента НЕ ДОЛЖЕН совпадать с IP сервера ===
            if (localIp == serverIp)
            {
                Console.WriteLine($"\n!!! ВНИМАНИЕ: Ваш IP {localIp} совпадает с IP сервера !!!");
                Console.WriteLine("Это допустимо ТОЛЬКО для теста на одном компьютере (localhost).");
                Console.WriteLine("В реальной сети используйте разные IP адреса.");
                Console.Write("Продолжить подключение? (y/n): ");
                if (Console.ReadLine()?.ToLower() != "y")
                {
                    Console.WriteLine("Подключение отменено.");
                    return;
                }
            }

            // === ПРОВЕРКА 2: Порт клиента НЕ ДОЛЖЕН совпадать с портом сервера ===
            if (localPort == tcpPort)
            {
                Console.WriteLine($"\n!!! ОШИБКА: Ваш локальный порт {localPort} совпадает с портом сервера {tcpPort} !!!");
                Console.WriteLine("Два разных приложения (сервер и клиент) не могут использовать один порт на одном IP.");
                Console.WriteLine("Используйте разные порты для сервера и клиента.");
                Console.WriteLine("Нажмите любую клавишу для выхода...");
                Console.ReadKey();
                return;
            }

            // === ПРОВЕРКА 3: Если IP одинаковые, то порты точно должны быть разными ===
            if (localIp == serverIp && localPort == tcpPort)
            {
                Console.WriteLine($"\n!!! ОШИБКА: На одном IP {localIp} порт {localPort} уже занят сервером !!!");
                Console.WriteLine("Невозможно подключиться, так как порт конфликтует с сервером.");
                Console.WriteLine("Нажмите любую клавишу для выхода...");
                Console.ReadKey();
                return;
            }

            // === ПРОВЕРКА 4: Доступность локального порта ===
            if (!IsLocalPortAvailable(localIp, localPort))
            {
                Console.WriteLine($"\n!!! ОШИБКА: Локальный порт {localPort} на адресе {localIp} уже используется !!!");
                Console.WriteLine("Возможно, другой клиент уже использует этот порт.");
                Console.WriteLine("Попробуйте другой порт (например, {localPort + 1})");
                Console.WriteLine("Нажмите любую клавишу для выхода...");
                Console.ReadKey();
                return;
            }

            Console.WriteLine("\nВсе проверки пройдены. Подключение...\n");

            try
            {
                tcpClient = new TcpClient(new IPEndPoint(IPAddress.Parse(localIp), localPort));
                await tcpClient.ConnectAsync(serverIp, tcpPort);
                stream = tcpClient.GetStream();

                byte[] nameData = Encoding.UTF8.GetBytes(userName);
                await stream.WriteAsync(nameData, 0, nameData.Length);

                isConnected = true;
                Console.WriteLine($"Подключено к серверу {serverIp}:{tcpPort} с вашего IP {localIp}:{localPort}");

                receiveThread = new Thread(ReceiveMessages);
                receiveThread.Start();

                await SendMessages();
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
                {
                    Console.WriteLine($"Ошибка: Локальный порт {localPort} уже используется!");
                }
                else if (ex.SocketErrorCode == SocketError.ConnectionRefused)
                {
                    Console.WriteLine($"Ошибка: Сервер {serverIp}:{tcpPort} не отвечает. Запущен ли сервер?");
                }
                else
                {
                    Console.WriteLine($"Ошибка: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка: {ex.Message}");
            }
            finally
            {
                Disconnect();
            }
        }

        private bool IsLocalPortAvailable(string ip, int port)
        {
            try
            {
                TcpClient testClient = new TcpClient(new IPEndPoint(IPAddress.Parse(ip), port));
                testClient.Close();
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        private void ReceiveMessages()
        {
            byte[] buffer = new byte[4096];

            while (isConnected && tcpClient.Connected)
            {
                try
                {
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (message.StartsWith("MSG|"))
                    {
                        Console.WriteLine(message.Substring(4));
                    }
                    else if (message.StartsWith("SYS|"))
                    {
                        string[] parts = message.Split('|');
                        if (parts.Length >= 4)
                        {
                            string type = parts[1];
                            string sysMessage = parts[3];

                            if (type == "connect")
                                Console.ForegroundColor = ConsoleColor.Green;
                            else if (type == "disconnect")
                                Console.ForegroundColor = ConsoleColor.Red;

                            Console.WriteLine(sysMessage);
                            Console.ResetColor();
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (isConnected)
                        Console.WriteLine($"Ошибка: {ex.Message}");
                    break;
                }
            }
        }

        private async Task SendMessages()
        {
            Console.WriteLine("Введите сообщения (или 'leave' для выхода):");

            while (isConnected)
            {
                string message = Console.ReadLine();

                if (message?.ToLower() == "leave")
                    break;

                if (!string.IsNullOrWhiteSpace(message))
                {
                    try
                    {
                        byte[] data = Encoding.UTF8.GetBytes(message);
                        await stream.WriteAsync(data, 0, data.Length);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Ошибка отправки: {ex.Message}");
                        break;
                    }
                }
            }
        }

        private void Disconnect()
        {
            isConnected = false;
            receiveThread?.Join(1000);
            stream?.Close();
            tcpClient?.Close();
            Console.WriteLine("Отключено от сервера");
        }
    }
}