using System.Net;
using System.Net.Sockets;
using System.Text;

namespace HttpProxyServer
{
    public class ProxyServer
    {
        private readonly IPAddress _address;
        private readonly int _port;
        private readonly BlacklistFilter _blacklist;
        private TcpListener _listener;

        private const int BufferSize = 16384;
        private const int BacklogSize = 50;

        public ProxyServer(IPAddress address, int port)
        {
            _address = address;
            _port = port;
            _blacklist = new BlacklistFilter("blacklist.txt");
        }

        public void Run()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start(BacklogSize);
            Console.WriteLine("Прокси запущен. Ожидание подключений...\n");

            while (true)
            {
                try
                {
                    TcpClient client = _listener.AcceptTcpClient();
                    Thread thread = new Thread(() => ProcessClient(client));
                    thread.IsBackground = true;
                    thread.Start();
                }
                catch { }
            }
        }

        private void ProcessClient(TcpClient client)
        {
            using (client)
            {
                try
                {
                    NetworkStream stream = client.GetStream();
                    string rawRequest = ReadHttpHeaders(stream);
                    if (string.IsNullOrEmpty(rawRequest)) return;

                    HttpRequest request = HttpRequest.Parse(rawRequest);
                    if (request == null) return;

                    if (_blacklist.IsBlocked(request.Host))
                    {
                        SendBlockedPage(stream, request.Host);
                        Console.WriteLine($"[BLOCKED] {request.Host}");
                        return;
                    }

                    ForwardRequest(stream, request);
                }
                catch { }
            }
        }

        private string ReadHttpHeaders(NetworkStream stream)
        {
            var sb = new StringBuilder();
            byte[] buf = new byte[BufferSize];

            stream.ReadTimeout = 5000;
            try
            {
                while (true)
                {
                    int n = stream.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    sb.Append(Encoding.ASCII.GetString(buf, 0, n));
                    if (sb.ToString().Contains("\r\n\r\n")) break;
                }
            }
            catch { }

            return sb.ToString();
        }

        private void ForwardRequest(NetworkStream clientStream, HttpRequest req)
        {
            TcpClient serverClient = null;
            try
            {
                serverClient = new TcpClient(AddressFamily.InterNetwork);
                var addresses = Dns.GetHostAddresses(req.TargetHost);
                var ipv4 = addresses.First(a => a.AddressFamily == AddressFamily.InterNetwork);
                serverClient.Connect(ipv4, req.TargetPort);
                NetworkStream serverStream = serverClient.GetStream();

                string rebuilt = RebuildRequest(req);
                byte[] reqBytes = Encoding.ASCII.GetBytes(rebuilt);
                serverStream.Write(reqBytes, 0, reqBytes.Length);

                byte[] buffer = new byte[BufferSize];
                bool firstRead = true;
                int bytesRead;

                serverStream.ReadTimeout = 15000;
                try
                {
                    while ((bytesRead = serverStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (firstRead)
                        {
                            LogResponse(req.OriginalUrl, buffer, bytesRead);
                            firstRead = false;
                        }
                        clientStream.Write(buffer, 0, bytesRead);
                    }
                }
                catch { }
            }
            catch { }
            finally
            {
                serverClient?.Close();
            }
        }

        private string RebuildRequest(HttpRequest req)
        {
            var sb = new StringBuilder();
            sb.Append($"{req.Method} {req.Path} {req.Version}\r\n");

            foreach (var kv in req.Headers)
            {
                if (kv.Key.Equals("Proxy-Connection", StringComparison.OrdinalIgnoreCase)) continue;
                if (kv.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append($"{kv.Key}: {kv.Value}\r\n");
            }

            sb.Append("Connection: close\r\n");
            sb.Append("\r\n");

            return sb.ToString();
        }

        private void LogResponse(string url, byte[] data, int length)
        {
            string text = Encoding.ASCII.GetString(data, 0, Math.Min(length, 512));
            int crlfIdx = text.IndexOf("\r\n");
            string statusLine = crlfIdx > 0 ? text.Substring(0, crlfIdx) : text;
            // Выводим только URL и код ответа как требует задание
            string[] statusParts = statusLine.Split(' ');
            string code = statusParts.Length >= 2 ? statusParts[1] : statusLine;
            Console.WriteLine($"{url} {code}");
        }

        private void SendBlockedPage(NetworkStream stream, string host)
        {
            string body = $"<html><body><h1>403 Доступ заблокирован</h1>" +
                          $"<p>Адрес <b>{host}</b> находится в чёрном списке.</p></body></html>";
            string response = $"HTTP/1.1 403 Forbidden\r\n" +
                              $"Content-Type: text/html; charset=utf-8\r\n" +
                              $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                              $"Connection: close\r\n\r\n{body}";
            byte[] bytes = Encoding.UTF8.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
        }
    }

    public class HttpRequest
    {
        public string Method { get; private set; }
        public string OriginalUrl { get; private set; }
        public string Path { get; private set; }
        public string Version { get; private set; }
        public string Host { get; private set; }
        public string TargetHost { get; private set; }
        public int TargetPort { get; private set; }
        public Dictionary<string, string> Headers { get; private set; }

        private HttpRequest() { }

        public static HttpRequest Parse(string raw)
        {
            string[] lines = raw.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 3) return null;

            var req = new HttpRequest
            {
                Method = parts[0],
                OriginalUrl = parts[1],
                Version = parts[2],
                Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            };

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) break;
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                string key = lines[i].Substring(0, colon).Trim();
                string val = lines[i].Substring(colon + 1).Trim();
                req.Headers[key] = val;
            }

            if (!req.Headers.TryGetValue("Host", out string hostHeader) || string.IsNullOrEmpty(hostHeader))
                return null;

            req.Host = hostHeader;

            if (hostHeader.Contains(':'))
            {
                var hpParts = hostHeader.Split(':', 2);
                req.TargetHost = hpParts[0];
                req.TargetPort = int.TryParse(hpParts[1], out int p) ? p : 80;
            }
            else
            {
                req.TargetHost = hostHeader;
                req.TargetPort = 80;
            }

            if (Uri.TryCreate(req.OriginalUrl, UriKind.Absolute, out Uri uri))
            {
                req.Path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                if (!uri.IsDefaultPort) req.TargetPort = uri.Port;
            }
            else
            {
                req.Path = req.OriginalUrl.StartsWith("/") ? req.OriginalUrl : "/" + req.OriginalUrl;
            }

            return req;
        }
    }

    public class BlacklistFilter
    {
        private readonly string _filePath;

        public BlacklistFilter(string filePath)
        {
            _filePath = filePath;
        }

        public bool IsBlocked(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;

            string normalized = Normalize(host);
            try
            {
                foreach (string line in File.ReadLines(_filePath))
                {
                    string entry = Normalize(line.Trim());
                    if (string.IsNullOrEmpty(entry) || entry.StartsWith("#")) continue;
                    if (normalized == entry || normalized.EndsWith("." + entry))
                        return true;
                }
            }
            catch (FileNotFoundException) { }
            catch { }

            return false;
        }

        private string Normalize(string input)
        {
            input = input.ToLower().Trim();
            if (input.StartsWith("http://"))  input = input[7..];
            if (input.StartsWith("https://")) input = input[8..];
            if (input.StartsWith("www."))     input = input[4..];
            int slash = input.IndexOf('/');
            if (slash >= 0) input = input[..slash];
            return input;
        }
    }
}
