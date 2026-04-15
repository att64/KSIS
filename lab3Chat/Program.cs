using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ChatServer
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var server = new ChatServer();
            await server.Start();
        }
    }

    public class ChatServer
    {
        private TcpListener tcpListener;
        private List<TcpClient> clients = new List<TcpClient>();
        private Dictionary<TcpClient, string> clientNames = new Dictionary<TcpClient, string>();
        private Dictionary<string, int> clientIPs = new Dictionary<string, int>();
        private bool isRunning = true;

        /// <summary>
        /// Проверяет, является ли строка корректным IPv4 адресом
        /// </summary>
        private bool IsValidIPv4(string ip)
        {
            if (!IPAddress.TryParse(ip, out IPAddress address))
                return false;

            // Проверяем, что это IPv4 (не IPv6)
            return address.AddressFamily == AddressFamily.InterNetwork;
        }

        /// <summary>
        /// Проверяет, является ли порт корректным числом и в диапазоне 1-65535
        /// </summary>
        private bool IsValidPort(int port)
        {
            return port >= 1 && port <= 65535;
        }

        /// <summary>
        /// Проверяет, является ли строка корректным числом
        /// </summary>
        private bool IsNumericString(string str)
        {
            return int.TryParse(str, out _);
        }

        public async Task Start()
        {
            Console.WriteLine("=== TCP ЧАТ СЕРВЕР ===");

            // === ВВОД IP АДРЕСА СЕРВЕРА С ПРОВЕРКОЙ ===
            string serverIp;
            IPAddress ipAddress;

            while (true)
            {
                Console.Write("Введите IP адрес сервера (например, 127.0.0.1): ");
                serverIp = Console.ReadLine();

                // Проверка: IP не должен быть пустым
                if (string.IsNullOrWhiteSpace(serverIp))
                {
                    Console.WriteLine("Ошибка: IP адрес не может быть пустым! Попробуйте снова.");
                    continue;
                }

                // Проверка: корректный ли IP адрес
                if (!IsValidIPv4(serverIp))
                {
                    Console.WriteLine($"Ошибка: '{serverIp}' - это не корректный IPv4 адрес! Попробуйте снова.");
                    Console.WriteLine("Примеры: 127.0.0.1, 192.168.1.100, 10.0.0.1");
                    continue;
                }

                ipAddress = IPAddress.Parse(serverIp);
                break;
            }

            // === ВВОД TCP ПОРТА С ПРОВЕРКАМИ ===
            int tcpPort;

            while (true)
            {
                Console.Write("Введите TCP порт для чата (число от 1 до 65535): ");
                string portInput = Console.ReadLine();

                // Проверка: порт не должен быть пустым
                if (string.IsNullOrWhiteSpace(portInput))
                {
                    Console.WriteLine("Ошибка: Порт не может быть пустым! Попробуйте снова.");
                    continue;
                }

                // Проверка: порт должен быть числом (не буквы и не символы)
                if (!IsNumericString(portInput))
                {
                    Console.WriteLine($"Ошибка: '{portInput}' - это не число! Введите цифры (например, 8888).");
                    continue;
                }

                tcpPort = int.Parse(portInput);

                // Проверка: порт должен быть в допустимом диапазоне
                if (!IsValidPort(tcpPort))
                {
                    Console.WriteLine($"Ошибка: Порт {tcpPort} недопустим! Используйте порты от 1 до 65535.");
                    continue;
                }

                break;
            }

            // Проверка доступности порта
            if (!IsTcpPortAvailable(serverIp, tcpPort))
            {
                Console.WriteLine($"Ошибка: Порт {tcpPort} на адресе {serverIp} уже используется!");
                Console.WriteLine("Нажмите любую клавишу для выхода...");
                Console.ReadKey();
                return;
            }

            try
            {
                // Запуск TCP сервера на указанном IP
                tcpListener = new TcpListener(ipAddress, tcpPort);
                tcpListener.Start();
                Console.WriteLine($"TCP сервер успешно запущен на {serverIp}:{tcpPort}");

                // Запуск обработки TCP подключений
                _ = Task.Run(AcceptClients);

                Console.WriteLine("Сервер запущен. Нажмите Enter для остановки...");
                Console.ReadLine();

                isRunning = false;
                Stop();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при запуске сервера: {ex.Message}");
            }
        }

        private bool IsTcpPortAvailable(string ip, int port)
        {
            try
            {
                TcpListener listener = new TcpListener(IPAddress.Parse(ip), port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private async Task AcceptClients()
        {
            while (isRunning)
            {
                try
                {
                    var tcpClient = await tcpListener.AcceptTcpClientAsync();

                    string clientIP = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address.ToString();
                    int clientPort = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Port;

                    // Проверка уникальности IP клиента
                    bool ipExists = false;
                    lock (clientIPs)
                    {
                        ipExists = clientIPs.ContainsKey(clientIP);
                    }

                    if (ipExists)
                    {
                        Console.WriteLine($"Отказано в подключении: клиент с IP {clientIP} уже подключен!");
                        tcpClient.Close();
                        continue;
                    }

                    lock (clients)
                    {
                        clients.Add(tcpClient);
                    }

                    lock (clientIPs)
                    {
                        clientIPs[clientIP] = clientPort;
                    }

                    _ = Task.Run(() => HandleClient(tcpClient, clientIP));
                }
                catch (Exception ex)
                {
                    if (isRunning)
                        Console.WriteLine($"Ошибка при подключении клиента: {ex.Message}");
                }
            }
        }

        private async Task HandleClient(TcpClient tcpClient, string clientIP)
        {
            string clientName = "Неизвестный";
            string clientEndPoint = tcpClient.Client.RemoteEndPoint?.ToString() ?? "Unknown";

            try
            {
                NetworkStream stream = tcpClient.GetStream();
                byte[] buffer = new byte[4096];

                int bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                clientName = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                lock (clientNames)
                {
                    clientNames[tcpClient] = clientName;
                }

                Console.WriteLine($"Клиент {clientName} подключился с адреса {clientEndPoint}");
                await BroadcastSystemMessage($"{clientName} присоединился к чату (IP: {clientIP})", "connect", clientName);

                while (isRunning && tcpClient.Connected)
                {
                    bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead == 0) break;

                    string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                    Console.WriteLine($"{clientName}: {message}");
                    await BroadcastMessage(message, clientName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при обработке клиента {clientName}: {ex.Message}");
            }
            finally
            {
                RemoveClient(tcpClient, clientIP);
                await BroadcastSystemMessage($"{clientName} покинул чат", "disconnect", clientName);
            }
        }

        private async Task BroadcastMessage(string message, string senderName)
        {
            string formattedMessage = $"{senderName}: {message}";
            byte[] data = Encoding.UTF8.GetBytes($"MSG|{formattedMessage}");

            List<TcpClient> clientsCopy;
            lock (clients)
            {
                clientsCopy = new List<TcpClient>(clients);
            }

            foreach (var client in clientsCopy)
            {
                try
                {
                    if (client.Connected)
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при отправке сообщения: {ex.Message}");
                }
            }
        }

        private async Task BroadcastSystemMessage(string message, string type, string clientName)
        {
            byte[] data = Encoding.UTF8.GetBytes($"SYS|{type}|{clientName}|{message}");

            List<TcpClient> clientsCopy;
            lock (clients)
            {
                clientsCopy = new List<TcpClient>(clients);
            }

            foreach (var client in clientsCopy)
            {
                try
                {
                    if (client.Connected)
                    {
                        var stream = client.GetStream();
                        await stream.WriteAsync(data, 0, data.Length);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при отправке системного сообщения: {ex.Message}");
                }
            }
        }

        private void RemoveClient(TcpClient client, string clientIP)
        {
            lock (clients)
            {
                clients.Remove(client);
            }
            lock (clientNames)
            {
                if (clientNames.ContainsKey(client))
                    clientNames.Remove(client);
            }
            lock (clientIPs)
            {
                if (clientIPs.ContainsKey(clientIP))
                    clientIPs.Remove(clientIP);
            }
            client.Close();
        }

        private void Stop()
        {
            tcpListener?.Stop();

            lock (clients)
            {
                foreach (var client in clients)
                {
                    client.Close();
                }
                clients.Clear();
            }

            Console.WriteLine("Сервер остановлен");
        }
    }
}