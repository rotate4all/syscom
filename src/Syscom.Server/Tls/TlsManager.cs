using System.Formats.Asn1;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Syscom.Server.Config;
using Syscom.Server.Discovery;
using Syscom.Shared;

namespace Syscom.Server.Tls;

/// <summary>
/// Optional HTTPS without a public domain: each PC creates its own small certificate authority once, and a server
/// certificate for its current LAN addresses signed by it. A phone trusts the CA once (setup page /trust), after
/// which the dashboard opens over HTTPS and can be installed as an app.
///
/// The CA is name-constrained to private addresses, *.local and this PC's name, so even a leaked CA key could not be
/// used to impersonate public websites. The server certificate is renewed automatically (addresses changed, or
/// less than 30 days left) without the phone having to trust anything again.
/// </summary>
public sealed class TlsManager : BackgroundService
{
    private const int LeafDays = 397;      // under Apple's 398-day limit for TLS server certificates
    private const int CaYears = 20;

    private readonly ILogger<TlsManager> _log;
    private readonly string _dir = Path.Combine(SyscomPaths.DataDir, "tls");
    private readonly object _gate = new();
    private X509Certificate2? _ca;
    private X509Certificate2? _leaf;

    public TlsManager(ILogger<TlsManager> log)
    {
        _log = log;
        Directory.CreateDirectory(_dir);
        _ca = LoadOrCreateCa();
        EnsureLeaf();
    }

    /// <summary>The certificate Kestrel presents (with private key).</summary>
    public X509Certificate2 Current
    {
        get { lock (_gate) return _leaf!; }
    }

    public byte[] CaCertificateDer => _ca!.RawData;
    public string CaFingerprint => Convert.ToHexString(SHA256.HashData(_ca!.RawData));
    public string CaName => _ca!.GetNameInfo(X509NameType.SimpleName, false);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Pick up new IP addresses (DHCP, Wi-Fi switch) and approaching expiry.
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) EnsureLeaf();
        }
        catch (OperationCanceledException) { }
    }

    private X509Certificate2 LoadOrCreateCa()
    {
        var file = Path.Combine(_dir, "ca.pfx");
        if (File.Exists(file))
        {
            var ca = X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable);
            if (ca.NotAfter > DateTime.Now.AddDays(LeafDays)) return ca;
            _log.LogWarning("Local CA is about to expire; creating a new one (phones must trust it again)");
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var name = new X500DistinguishedName($"CN=SYSCOM Local CA ({Environment.MachineName}), O=SYSCOM");
        var req = new CertificateRequest(name, key, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        req.CertificateExtensions.Add(NameConstraints());
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(CaYears));
        File.WriteAllBytes(file, created.Export(X509ContentType.Pkcs12));
        File.WriteAllBytes(Path.Combine(_dir, "ca.crt"), created.RawData);
        _log.LogInformation("Created local certificate authority {Name}", created.Subject);
        return X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable);
    }

    private void EnsureLeaf()
    {
        try
        {
            var (ips, dns) = CurrentNames();
            var file = Path.Combine(_dir, "server.pfx");
            var leaf = _leaf;
            if (leaf is null && File.Exists(file))
                leaf = X509CertificateLoader.LoadPkcs12FromFile(file, null, X509KeyStorageFlags.Exportable);

            if (leaf is not null && leaf.NotAfter > DateTime.Now.AddDays(30) && Covers(leaf, ips, dns) && IssuedByCa(leaf))
            {
                lock (_gate) _leaf = leaf;
                return;
            }

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var req = new CertificateRequest(new X500DistinguishedName($"CN={Environment.MachineName}, O=SYSCOM"), key, HashAlgorithmName.SHA256);
            var san = new SubjectAlternativeNameBuilder();
            foreach (var ip in ips) san.AddIpAddress(ip);
            foreach (var d in dns) san.AddDnsName(d);
            req.CertificateExtensions.Add(san.Build(false));
            req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
            req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
            req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(_ca!, true, false));

            var notAfter = DateTimeOffset.UtcNow.AddDays(LeafDays);
            if (notAfter > _ca!.NotAfter) notAfter = _ca.NotAfter.AddDays(-1);
            using var signed = req.Create(_ca, DateTimeOffset.UtcNow.AddDays(-1), notAfter, RandomNumberGenerator.GetBytes(16));
            using var withKey = signed.CopyWithPrivateKey(key);

            // Round-trip through PKCS#12 so Windows' TLS stack gets a persisted (non-ephemeral) key.
            var pfx = withKey.Export(X509ContentType.Pkcs12);
            File.WriteAllBytes(file, pfx);
            var fresh = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
            lock (_gate) _leaf = fresh;
            _log.LogInformation("HTTPS certificate issued for {Names}", string.Join(", ", ips.Select(i => i.ToString()).Concat(dns)));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Could not prepare the HTTPS certificate");
            if (_leaf is null) throw;
        }
    }

    private bool IssuedByCa(X509Certificate2 leaf) => leaf.Issuer == _ca!.Subject;

    private static bool Covers(X509Certificate2 cert, IReadOnlyList<IPAddress> ips, IReadOnlyList<string> dns)
    {
        var ext = cert.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
        if (ext is null) return false;
        var haveIps = ext.EnumerateIPAddresses().ToHashSet();
        var haveDns = ext.EnumerateDnsNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return ips.All(haveIps.Contains) && dns.All(haveDns.Contains);
    }

    private static (IReadOnlyList<IPAddress> Ips, IReadOnlyList<string> Dns) CurrentNames()
    {
        var ips = LanDiscovery.LocalAddresses().Select(IPAddress.Parse)
            .Where(IsPermitted)
            .Append(IPAddress.Loopback)
            .Distinct()
            .OrderBy(i => i.ToString(), StringComparer.Ordinal)
            .ToList();
        var host = Environment.MachineName.ToLowerInvariant();
        var dns = new List<string> { "localhost", host, $"{host}.local" };
        return (ips, dns);
    }

    // ---- name constraints (RFC 5280 §4.2.1.10)

    private static readonly (byte[] Net, byte[] Mask)[] PermittedV4 =
    {
        (new byte[] { 10, 0, 0, 0 }, new byte[] { 255, 0, 0, 0 }),
        (new byte[] { 172, 16, 0, 0 }, new byte[] { 255, 240, 0, 0 }),
        (new byte[] { 192, 168, 0, 0 }, new byte[] { 255, 255, 0, 0 }),
        (new byte[] { 100, 64, 0, 0 }, new byte[] { 255, 192, 0, 0 }),   // CGNAT / Tailscale-style VPNs
        (new byte[] { 169, 254, 0, 0 }, new byte[] { 255, 255, 0, 0 }),  // link-local
        (new byte[] { 127, 0, 0, 0 }, new byte[] { 255, 0, 0, 0 }),
    };

    private static bool IsPermitted(IPAddress ip)
    {
        if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return PermittedV4.Any(p => Enumerable.Range(0, 4).All(i => (b[i] & p.Mask[i]) == p.Net[i]));
    }

    private static X509Extension NameConstraints()
    {
        var host = Environment.MachineName.ToLowerInvariant();
        var w = new AsnWriter(AsnEncodingRules.DER);
        using (w.PushSequence())                                            // NameConstraints
        {
            using (w.PushSequence(new Asn1Tag(TagClass.ContextSpecific, 0)))  // permittedSubtrees
            {
                foreach (var d in new[] { "localhost", host, "local" })
                {
                    using (w.PushSequence())                                // GeneralSubtree
                        w.WriteCharacterString(UniversalTagNumber.IA5String, d, new Asn1Tag(TagClass.ContextSpecific, 2)); // dNSName
                }
                foreach (var (net, mask) in PermittedV4)
                {
                    using (w.PushSequence())
                        w.WriteOctetString(net.Concat(mask).ToArray(), new Asn1Tag(TagClass.ContextSpecific, 7));        // iPAddress
                }
            }
        }
        return new X509Extension("2.5.29.30", w.Encode(), critical: true);
    }

    // ---- iOS configuration profile containing the CA

    public string MobileConfig()
    {
        var uuid1 = DeterministicUuid("payload");
        var uuid2 = DeterministicUuid("profile");
        var b64 = Convert.ToBase64String(_ca!.RawData);
        var sb = new StringBuilder();
        sb.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
              <key>PayloadContent</key><array><dict>
                <key>PayloadCertificateFileName</key><string>syscom-ca.cer</string>
                <key>PayloadContent</key><data>
            """);
        sb.Append(b64).Append('\n');
        sb.Append($"""
                </data>
                <key>PayloadDescription</key><string>Lets this device open SYSCOM on {Environment.MachineName} over HTTPS.</string>
                <key>PayloadDisplayName</key><string>{CaName}</string>
                <key>PayloadIdentifier</key><string>com.syscom.ca.{uuid1}</string>
                <key>PayloadType</key><string>com.apple.security.root</string>
                <key>PayloadUUID</key><string>{uuid1}</string>
                <key>PayloadVersion</key><integer>1</integer>
              </dict></array>
              <key>PayloadDisplayName</key><string>SYSCOM ({Environment.MachineName})</string>
              <key>PayloadIdentifier</key><string>com.syscom.profile.{uuid2}</string>
              <key>PayloadRemovalDisallowed</key><false/>
              <key>PayloadType</key><string>Configuration</string>
              <key>PayloadUUID</key><string>{uuid2}</string>
              <key>PayloadVersion</key><integer>1</integer>
            </dict></plist>
            """);
        return sb.ToString();
    }

    private string DeterministicUuid(string salt)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(salt + CaFingerprint));
        return new Guid(h.AsSpan(0, 16)).ToString().ToUpperInvariant();
    }
}
