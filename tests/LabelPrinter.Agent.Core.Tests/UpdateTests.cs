using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using LabelPrinter.Agent.Core.Tests.Harness;
using LabelPrinter.Agent.Core.Updates;

namespace LabelPrinter.Agent.Core.Tests;

public sealed class UpdateManifestTests
{
    private static readonly string Sha = new('a', 64);

    private static (byte[] Manifest, byte[] Sig, byte[] Pub) Make(Func<UpdateOffer, UpdateOffer>? edit = null, ECDsa? signer = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var offer = new UpdateOffer("0.2.3-exp.1", UpdateManifest.InstallerFileName("0.2.3-exp.1"), 12345, Sha, new DateTimeOffset(2026, 11, 21, 0, 0, 0, TimeSpan.Zero), "Fixes");
        if (edit is not null) offer = edit(offer);
        var manifest = UpdateManifest.Create(offer);
        var sig = UpdateManifest.Sign(manifest, signer ?? key);
        return (manifest, sig, key.ExportSubjectPublicKeyInfo());
    }

    private static string CodeOf(Action a)
    {
        try { a(); } catch (UpdateRejectedException ex) { return ex.Code; }
        return "ACCEPTED";
    }

    [Test] public void VersionsOrderLikeSemVer()
    {
        int C(string a, string b) => Math.Sign(AgentVersion.Parse(a).CompareTo(AgentVersion.Parse(b)));
        Check.Equal(1, C("0.2.2-exp.1", "0.2.1-exp.1"), "patch");
        Check.Equal(1, C("0.3.0", "0.3.0-exp.9"), "release beats its pre-release");
        Check.Equal(1, C("1.0.0-exp.10", "1.0.0-exp.2"), "numeric identifiers compare as numbers");
        Check.Equal(-1, C("1.0.0-alpha", "1.0.0-alpha.1"), "shorter pre-release is older");
        Check.Equal(0, C("0.2.2-exp.1+abc", "0.2.2-exp.1"), "build metadata ignored");
        Check.True(!AgentVersion.TryParse("0.2", out _) && !AgentVersion.TryParse("v1.0.0", out _) && !AgentVersion.TryParse("01.0.0", out _), "malformed versions rejected");
    }

    [Test] public void ValidSignatureAndNewerVersionIsAccepted()
    {
        var (m, s, pub) = Make();
        var offer = UpdateManifest.Verify(m, s, [pub], "0.2.2-exp.1");
        Check.Equal("0.2.3-exp.1", offer.Version, "version");
        Check.Equal(12345L, offer.SizeBytes, "size");
        Check.Equal("Fixes", offer.Notes, "notes");
    }

    [Test] public void SecondKeyOfTheRotationPairAlsoWorks()
    {
        var (m, s, pub) = Make();
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Check.Equal("0.2.3-exp.1", UpdateManifest.Verify(m, s, [other.ExportSubjectPublicKeyInfo(), pub], "0.2.2-exp.1").Version, "second key");
    }

    [Test] public void AnyChangeToTheManifestBreaksTheSignature()
    {
        var (m, s, pub) = Make();
        var tampered = (byte[])m.Clone();
        tampered[tampered.Length / 2] ^= 0x01;
        Check.Equal("SIGNATURE_INVALID", CodeOf(() => UpdateManifest.Verify(tampered, s, [pub], "0.2.2-exp.1")), "tampered manifest");
        var badSig = (byte[])s.Clone(); badSig[10] ^= 0x01;
        Check.Equal("SIGNATURE_INVALID", CodeOf(() => UpdateManifest.Verify(m, badSig, [pub], "0.2.2-exp.1")), "tampered signature");
        Check.Equal("SIGNATURE_FORMAT", CodeOf(() => UpdateManifest.Verify(m, s[..63], [pub], "0.2.2-exp.1")), "short signature");
    }

    [Test] public void ASignatureFromAnotherKeyIsRefused()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (m, _, pub) = Make();
        Check.Equal("SIGNATURE_INVALID", CodeOf(() => UpdateManifest.Verify(m, UpdateManifest.Sign(m, attacker), [pub], "0.2.2-exp.1")), "attacker key");
    }

    [Test] public void NoBuiltInKeyMeansNothingIsEverAccepted()
    {
        var (m, s, _) = Make();
        Check.Equal("NO_TRUSTED_KEY", CodeOf(() => UpdateManifest.Verify(m, s, [], "0.2.2-exp.1")), "no key");
        Check.Equal("SIGNATURE_INVALID", CodeOf(() => UpdateManifest.Verify(m, s, [[1, 2, 3]], "0.2.2-exp.1")), "garbage key");
    }

    [Test] public void OnlyStrictlyNewerVersionsAreOffered()
    {
        var (m, s, pub) = Make();
        Check.Equal("NOT_NEWER", CodeOf(() => UpdateManifest.Verify(m, s, [pub], "0.2.3-exp.1")), "same version");
        Check.Equal("NOT_NEWER", CodeOf(() => UpdateManifest.Verify(m, s, [pub], "0.3.0")), "downgrade / replay of an old manifest");
        Check.Equal("CURRENT_VERSION", CodeOf(() => UpdateManifest.Verify(m, s, [pub], "garbage")), "unreadable own version");
    }

    [Test] public void ASignedButMalformedManifestIsStillRefused()
    {
        string Code(Func<UpdateOffer, UpdateOffer> edit) { var (m, s, pub) = Make(edit); return CodeOf(() => UpdateManifest.Verify(m, s, [pub], "0.2.2-exp.1")); }
        Check.Equal("MANIFEST_FILE", Code(o => o with { File = "evil.exe" }), "file name");
        Check.Equal("MANIFEST_FILE", Code(o => o with { File = "..\\..\\evil-win-x64-unsigned-setup.exe" }), "path in file name");
        Check.Equal("MANIFEST_FILE", Code(o => o with { File = UpdateManifest.InstallerFileName("0.2.4-exp.1") }), "file of another version");
        Check.Equal("MANIFEST_SHA256", Code(o => o with { Sha256 = "XYZ" }), "hash");
        Check.Equal("MANIFEST_SHA256", Code(o => o with { Sha256 = new string('A', 64) }), "upper-case hash");
        Check.Equal("MANIFEST_SIZEBYTES", Code(o => o with { SizeBytes = 0 }), "zero size");
        Check.Equal("MANIFEST_SIZEBYTES", Code(o => o with { SizeBytes = UpdateManifest.MaxInstallerBytes + 1 }), "huge size");
        Check.Equal("MANIFEST_VERSION", Code(o => o with { Version = "latest", File = UpdateManifest.InstallerFileName("latest") }), "version");
    }

    [Test] public void OtherChannelAndSchemaAreRefused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        foreach (var (json, code) in new[]
        {
            ("""{"schema":1,"channel":"stable","version":"0.2.3","file":"x","sizeBytes":1,"sha256":"a","issuedAt":"2026-11-21T00:00:00Z"}""", "MANIFEST_CHANNEL"),
            ("""{"schema":2,"channel":"experimental"}""", "MANIFEST_SCHEMA"),
            ("[1,2]", "MANIFEST_SHAPE"),
            ("{not json", "MANIFEST_JSON"),
        })
        {
            var m = Encoding.UTF8.GetBytes(json);
            Check.Equal(code, CodeOf(() => UpdateManifest.Verify(m, UpdateManifest.Sign(m, key), [key.ExportSubjectPublicKeyInfo()], "0.2.2-exp.1")), json);
        }
    }

    [Test] public void NotesAreCappedAndStrippedOfControlCharacters()
    {
        var (m, s, pub) = Make(o => o with { Notes = "a\u0007b\r\n" + new string('x', 1000) });
        var offer = UpdateManifest.Verify(m, s, [pub], "0.2.2-exp.1");
        Check.True(offer.Notes.Length <= UpdateManifest.MaxNotesChars, "capped");
        Check.True(!offer.Notes.Any(c => char.IsControl(c) && c != '\n'), "no control characters");
    }
}

/// <summary>The update flow end to end over real loopback HTTP: check, user confirmation, download, verification, launch.</summary>
public sealed class UpdateFlowTests : IDisposable
{
    private sealed class FileServer : IDisposable
    {
        private readonly HttpListener _l = new();
        private readonly Thread _t;
        public readonly string Origin;
        public readonly Dictionary<string, (int Status, byte[] Body, string? Location)> Routes = new();
        public readonly List<string> Hits = [];
        public FileServer()
        {
            var p = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); p.Start();
            var port = ((IPEndPoint)p.LocalEndpoint).Port; p.Stop();
            Origin = $"http://127.0.0.1:{port}";
            _l.Prefixes.Add(Origin + "/"); _l.Start();
            _t = new Thread(() =>
            {
                while (_l.IsListening)
                {
                    HttpListenerContext c;
                    try { c = _l.GetContext(); } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                    lock (Hits) Hits.Add(c.Request.Url!.AbsolutePath);
                    if (!Routes.TryGetValue(c.Request.Url!.AbsolutePath, out var r)) r = (404, [], null);
                    c.Response.StatusCode = r.Status;
                    if (r.Location is not null) c.Response.RedirectLocation = r.Location;
                    c.Response.ContentLength64 = r.Body.Length;
                    try { c.Response.OutputStream.Write(r.Body); c.Response.Close(); } catch (Exception ex) when (ex is HttpListenerException or IOException) { }
                }
            }) { IsBackground = true };
            _t.Start();
        }
        public void Dispose() { try { _l.Stop(); _l.Close(); } catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException) { } _t.Join(2000); }
    }

    private const string Current = "0.2.2-exp.1";
    private const string Next = "0.2.3-exp.1";
    private readonly PanelBench _b = new();
    private readonly FileServer _srv = new();
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _installer = RandomNumberGenerator.GetBytes(4096);

    public UpdateFlowTests() => Publish(Next, _installer, null);

    private void Publish(string version, byte[] served, Func<UpdateOffer, UpdateOffer>? edit, ECDsa? signer = null)
    {
        var offer = new UpdateOffer(version, UpdateManifest.InstallerFileName(version), _installer.Length, Convert.ToHexString(SHA256.HashData(_installer)).ToLowerInvariant(), DateTimeOffset.UtcNow, "Test notes");
        if (edit is not null) offer = edit(offer);
        var manifest = UpdateManifest.Create(offer);
        _srv.Routes["/downloads/agent-update.json"] = (200, manifest, null);
        _srv.Routes["/downloads/agent-update.sig"] = (200, Encoding.ASCII.GetBytes(Convert.ToBase64String(UpdateManifest.Sign(manifest, signer ?? _key))), null);
        _srv.Routes["/downloads/" + offer.File] = (200, served, null);
    }

    private UpdateService Service(bool withKey = true, string current = Current)
    {
        var keys = new List<byte[]>();
        if (withKey) keys.Add(_key.ExportSubjectPublicKeyInfo());
        var svc = new UpdateService(_b.Host, _b.Platform, new FileLog(_b.Dir, echo: false),
            new UpdateOptions(new Uri(_srv.Origin), keys, current, TimeSpan.FromHours(1), TimeSpan.FromHours(1)));
        _b.Service.Updates = svc;
        return svc;
    }

    public void Dispose() { _srv.Dispose(); _key.Dispose(); _b.Dispose(); }

    [Test] public void CheckingAnnouncesTheNewVersionAndDownloadsNothing()
    {
        var svc = Service();
        Check.True(svc.CheckNow().Ok, "check");
        Check.Equal(UpdateStatus.Available, svc.Status, "available");
        Check.Equal(Next, svc.Offer!.Version, "version");
        Check.True(!_srv.Hits.Any(h => h.EndsWith(".exe")), "the installer is not downloaded before the user asks");
        Check.True(_b.Platform.Installers.Count == 0, "nothing launched");
        var st = _b.State()["update"]!;
        Check.Equal("Available", st["status"]!.GetValue<string>(), "state status");
        Check.Equal(Next, st["version"]!.GetValue<string>(), "state version");
    }

    [Test] public void InstallDownloadsVerifiesAndLaunchesOnlyAfterConfirmation()
    {
        var svc = Service(); svc.CheckNow();
        var refused = _b.Post("/api/update/install", new { });
        Check.True(refused.Status == 400 && _b.Platform.Installers.Count == 0, "no confirmation, no install");
        var ok = _b.Post("/api/update/install", new { confirm = true });
        Check.True(ok.Status == 200, "install: " + ok.Body);
        var (path, args) = _b.Platform.Installers.Single();
        Check.True(path.StartsWith(Path.Combine(_b.Dir, "update"), StringComparison.Ordinal), "installer lives in the data directory's update folder: " + path);
        Check.True(File.ReadAllBytes(path).AsSpan().SequenceEqual(_installer), "bytes are the signed ones");
        Check.True(args.Contains("/VERYSILENT") && args.Contains("/UPDATE=1") && args.Contains("/NORESTART"), "silent upgrade arguments: " + args);
        Check.Equal(UpdateStatus.Installing, svc.Status, "installing");
    }

    [Test] public void TamperedInstallerIsNeverLaunched()
    {
        var evil = (byte[])_installer.Clone(); evil[100] ^= 0xFF;
        Publish(Next, evil, null); // signed hash is of the real installer, the server serves different bytes
        var svc = Service(); svc.CheckNow();
        Check.True(!svc.Install().Ok, "refused");
        Check.Equal(UpdateStatus.Failed, svc.Status, "failed");
        Check.True(_b.Platform.Installers.Count == 0, "nothing launched");
        Check.True(!Directory.Exists(Path.Combine(_b.Dir, "update")) || Directory.GetFiles(Path.Combine(_b.Dir, "update")).Length == 0, "no leftover file");
    }

    [Test] public void WrongSizeInEitherDirectionIsRefused()
    {
        foreach (var served in new[] { _installer.Concat(new byte[] { 1 }).ToArray(), _installer[..^1] })
        {
            Publish(Next, served, null);
            var svc = Service(); svc.CheckNow();
            Check.True(!svc.Install().Ok && _b.Platform.Installers.Count == 0, "size " + served.Length);
        }
    }

    [Test] public void ManifestSignedByAnotherKeyIsIgnored()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Publish(Next, _installer, null, attacker);
        var svc = Service();
        Check.True(!svc.CheckNow().Ok, "refused");
        Check.Equal(UpdateStatus.Failed, svc.Status, "failed");
        Check.True(svc.Offer is null && !svc.Install().Ok, "no offer to install");
    }

    [Test] public void NoDowngradeAndNoUpdateWhenAlreadyCurrent()
    {
        var svc = Service(current: Next);
        Check.True(svc.CheckNow().Ok, "check");
        Check.Equal(UpdateStatus.UpToDate, svc.Status, "up to date");
        Check.True(!svc.Install().Ok, "nothing to install");
    }

    [Test] public void RedirectsAreNotFollowed()
    {
        _srv.Routes["/downloads/agent-update.json"] = (302, [], "http://127.0.0.1:1/evil");
        var svc = Service();
        Check.True(!svc.CheckNow().Ok && svc.Offer is null, "redirect refused");
        Check.Equal(UpdateStatus.Failed, svc.Status, "failed");
    }

    [Test] public void WithoutABuiltInKeyTheFeatureIsOffAndSaysSo()
    {
        var svc = Service(withKey: false);
        Check.Equal(UpdateStatus.Disabled, svc.Status, "disabled");
        Check.True(!svc.CheckNow().Ok && _srv.Hits.Count == 0, "does not even contact the server");
        Check.Equal("Disabled", _b.State()["update"]!["status"]!.GetValue<string>(), "state");
    }

    [Test] public void PlainHttpToARemoteHostIsRefused()
    {
        var threw = false;
        try { _ = new UpdateService(_b.Host, _b.Platform, new FileLog(_b.Dir, echo: false), new UpdateOptions(new Uri("http://updates.example"), [_key.ExportSubjectPublicKeyInfo()], Current, TimeSpan.FromHours(1), TimeSpan.FromHours(1))); }
        catch (ArgumentException) { threw = true; }
        Check.True(threw, "http to a non-loopback host is refused");
    }

    [Test] public void StateNeverCarriesAnAddress()
    {
        var svc = Service(); svc.CheckNow();
        var raw = _b.Http.GetStringAsync("/api/state").Result;
        Check.True(!raw.Contains(_srv.Origin, StringComparison.Ordinal) && !raw.Contains("/downloads/", StringComparison.Ordinal), "no update address in the state");
    }
}
