using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading.Channels;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IronPaw.AgentCore.Mcp;

internal sealed class HostedMcpTransport(string name, McpHostTransport options) : IClientTransport
{
    public string Name => name;
    public static void Validate(McpHostTransport options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.MaxFrameBytes is < 4096 or > 1_048_576 || options.MaxSessionBytes < options.MaxFrameBytes ||
            options.MaxSessionBytes > 67_108_864 || options.MaxSessionFrames is < 8 or > 8192)
            throw new ArgumentException("Connector traffic limits are outside the supported ranges.");
        if (options.Kind == "stdio")
        {
            if (!OperatingSystem.IsWindows()) throw new ArgumentException("Hosted stdio connectors require Windows job limits. Other platforms can use HTTP or an embedding host's reviewed transport.");
            var limits = options.ProcessLimits ?? new();
            if (limits.MaxMemoryMiB is < 64 or > 4096 || limits.MaxProcesses is < 1 or > 32 || limits.MaxCpuPercent is < 1 or > 100)
                throw new ArgumentException("Connector process limits are outside their supported ranges.");
            if (string.IsNullOrWhiteSpace(options.Command) || !Path.IsPathFullyQualified(options.Command) ||
                string.IsNullOrWhiteSpace(options.WorkingDirectory) || !Path.IsPathFullyQualified(options.WorkingDirectory) ||
                !Directory.Exists(options.WorkingDirectory) || options.ExecutableSha256 is null ||
                options.ExecutableSha256.Length != 64 || options.ExecutableSha256.Any(c => !char.IsAsciiHexDigit(c)) ||
                options.Arguments?.Count > 128 || options.Arguments?.Any(a => a is null || a.Length > 4096 || a.Contains('\0')) == true ||
                options.Endpoint is not null || options.BearerTokenEnvironmentVariable is not null || options.AllowPrivateNetwork)
                throw new ArgumentException("stdio requires an absolute executable, SHA-256 and working directory, with no HTTP settings.");
            if (options.EnvironmentVariables?.Count > 32 || options.EnvironmentVariables?.Any(pair => !ValidEnvironmentName(pair.Key) || !ValidEnvironmentName(pair.Value)) == true)
                throw new ArgumentException("Connector environment mappings require explicit environment-variable names.");
            using var verified = VerifyExecutable(options);
            using var inputs = OpenReviewedFiles(options);
        }
        else if (options.Kind == "http")
        {
            if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length != 0 ||
                endpoint.Fragment.Length != 0 || endpoint.Query.Length != 0 ||
                (endpoint.Scheme != "https" && !(endpoint.Scheme == "http" && endpoint.IsLoopback && options.AllowPrivateNetwork)) ||
                options.Command is not null || options.Arguments is not null || options.WorkingDirectory is not null ||
                options.ExecutableSha256 is not null || options.EnvironmentVariables is not null ||
                options.ReviewedFiles is not null || options.ProcessLimits is not null ||
                (options.BearerTokenEnvironmentVariable is not null && !ValidEnvironmentName(options.BearerTokenEnvironmentVariable)))
                throw new ArgumentException("HTTP requires an exact HTTPS endpoint without user info, query or fragment. Explicit loopback HTTP is allowed for local servers.");
        }
        else throw new ArgumentException("Connector transport kind must be stdio or http.");
        if ((options.EnvironmentVariables?.Count > 0 || options.BearerTokenEnvironmentVariable is not null) &&
            (string.IsNullOrWhiteSpace(options.CredentialIdentity) || options.CredentialIdentity.Length > 256))
            throw new ArgumentException("Secret references require a nonsecret credentialIdentity; change it when the granted credential identity changes.");
    }

    private static bool ValidEnvironmentName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

    private static ReviewedFileLease OpenReviewedFiles(McpHostTransport options)
    {
        if (options.ReviewedFiles?.Count > 64) throw new ArgumentException("A connector can bind at most 64 reviewed launch files.");
        var lease = new ReviewedFileLease();
        long bytes = 0;
        try
        {
            foreach (var file in options.ReviewedFiles ?? new Dictionary<string, string>())
            {
                if (!Path.IsPathFullyQualified(file.Key) || file.Value is null || file.Value.Length != 64 || file.Value.Any(c => !char.IsAsciiHexDigit(c)))
                    throw new ArgumentException("Reviewed launch files require absolute paths and SHA-256 hashes.");
                var stream = new FileStream(file.Key, FileMode.Open, FileAccess.Read, FileShare.Read);
                lease.Files.Add(stream);
                bytes += stream.Length;
                if (bytes > 536_870_912) throw new ArgumentException("Reviewed launch inputs exceed 512 MiB.");
                if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(stream)), file.Value, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("A reviewed connector launch file changed. Review its new content before enabling it.");
            }
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static FileStream VerifyExecutable(McpHostTransport options)
    {
        var file = new FileStream(options.Command!, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 536_870_912 || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(file)), options.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
        {
            file.Dispose();
            throw new ArgumentException("Connector executable hash changed. Review the executable and update the binding before enabling it.");
        }
        return file;
    }

    public async Task<ITransport> ConnectAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var budget = new McpTrafficBudget(options.MaxSessionBytes, options.MaxSessionFrames);
        if (options.Kind == "http")
        {
            var endpoint = new Uri(options.Endpoint!);
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                AutomaticDecompression = DecompressionMethods.All,
                MaxResponseHeadersLength = 32,
                MaxConnectionsPerServer = 2,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                ConnectCallback = async (context, ct) =>
                {
                    var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
                    var address = addresses.FirstOrDefault(ip => options.AllowPrivateNetwork || PublicAddress(ip))
                        ?? throw new IOException("Connector endpoint resolves only to addresses outside its network grant.");
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
                    catch { socket.Dispose(); throw; }
                }
            };
            var client = new HttpClient(new BoundHttpHandler(endpoint, options, budget) { InnerHandler = handler }) { Timeout = Timeout.InfiniteTimeSpan };
            try
            {
                var transport = new HttpClientTransport(new HttpClientTransportOptions
                {
                    Endpoint = endpoint,
                    Name = name,
                    TransportMode = HttpTransportMode.StreamableHttp,
                    EnableStandaloneGetStream = false,
                    MaxReconnectionAttempts = 0
                }, client, ownsHttpClient: true);
                return new OwnedHttpTransport(await transport.ConnectAsync(cancellationToken).ConfigureAwait(false), client);
            }
            catch { client.Dispose(); throw; }
        }

        using var executable = VerifyExecutable(options); // Recheck at dispatch and deny replacement while launching.
        var start = new ProcessStartInfo(options.Command!)
        {
            WorkingDirectory = options.WorkingDirectory!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        foreach (var pair in options.EnvironmentVariables ?? new Dictionary<string, string>())
            start.Environment[pair.Key] = Environment.GetEnvironmentVariable(pair.Value)
                ?? throw new ArgumentException("A configured connector environment reference is missing.");
        foreach (var argument in options.Arguments ?? []) start.ArgumentList.Add(argument);
        var inputs = OpenReviewedFiles(options);
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("Connector process could not start."); }
        catch { inputs.Dispose(); throw; }
        WindowsConnectorJob? job = null;
        try
        {
            job = new(process, options.ProcessLimits ?? new());
            var stderr = DrainErrorAsync(process.StandardError.BaseStream, budget, process);
            var transport = await new StreamClientTransport(process.StandardInput.BaseStream,
                new BoundedMcpStream(process.StandardOutput.BaseStream, options.MaxFrameBytes, budget)).ConnectAsync(cancellationToken).ConfigureAwait(false);
            return new OwnedProcessTransport(transport, process, stderr, job, inputs);
        }
        catch { Kill(process); job?.Dispose(); inputs.Dispose(); process.Dispose(); throw; }
    }

    private static async Task DrainErrorAsync(Stream stream, McpTrafficBudget budget, Process process)
    {
        // Never retain or forward stderr, which can contain arbitrary credentials and giant lines.
        var buffer = new byte[4096];
        try { int read; while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) != 0) budget.Charge(read, 0); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { Kill(process); }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    internal static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4) return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
            !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
            !(bytes[0] == 192 && bytes[1] == 168) && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return (bytes[0] & 0xe0) == 0x20; // Public IPv6 global unicast only.
    }

    private sealed class ReviewedFileLease : IDisposable
    {
        public List<FileStream> Files { get; } = [];
        public void Dispose() { foreach (var file in Files) file.Dispose(); }
    }

    private sealed class OwnedProcessTransport(ITransport inner, Process process, Task stderr, WindowsConnectorJob job, ReviewedFileLease inputs) : ITransport
    {
        public string? SessionId => inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => inner.SendMessageAsync(message, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            Kill(process);
            try
            {
                await inner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                await stderr.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            finally { job.Dispose(); inputs.Dispose(); process.Dispose(); }
        }
    }

    private sealed class OwnedHttpTransport(ITransport inner, HttpClient client) : ITransport
    {
        public string? SessionId => inner.SessionId;
        public ChannelReader<JsonRpcMessage> MessageReader => inner.MessageReader;
        public Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default) => inner.SendMessageAsync(message, cancellationToken);
        public async ValueTask DisposeAsync()
        {
            try { await inner.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            finally { client.Dispose(); }
        }
    }

    private sealed class BoundHttpHandler(Uri endpoint, McpHostTransport options, McpTrafficBudget budget) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri != endpoint) throw new IOException("Connector attempted an endpoint outside its reviewed binding.");
            if (options.BearerTokenEnvironmentVariable is { } variable)
            {
                var secret = Environment.GetEnvironmentVariable(variable);
                if (string.IsNullOrWhiteSpace(secret) || secret.Length > 16_384 || secret.Any(char.IsControl))
                    throw new ArgumentException("The connector bearer-token environment reference is missing or invalid.");
                request.Headers.Authorization = new("Bearer", secret);
            }
            budget.Charge(0, 1);
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            try
            {
                if ((int)response.StatusCode is >= 300 and < 400) throw new IOException("Connector redirects require a newly reviewed endpoint.");
                if (response.Content.Headers.ContentLength > options.MaxSessionBytes) throw new IOException("Connector response exceeds its traffic grant.");
                var original = response.Content;
                var bounded = new StreamContent(new BoundedMcpStream(await original.ReadAsStreamAsync(ct).ConfigureAwait(false), options.MaxFrameBytes, budget));
                foreach (var header in original.Headers) bounded.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content = bounded;
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }
}
