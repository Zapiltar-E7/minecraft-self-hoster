using System.Security.Cryptography;
using System.Text;

// Signs release builds so the app can tell an update really came from whoever holds the private key.
//   keygen            makes a new key pair (once) and prints the public key to put in Updater.PublicKey
//   sign <exe> <ver>  writes <exe>.sig, to attach to the GitHub release next to the exe
//   verify <exe> <ver> checks an exe against its .sig
// The private key stays on this PC (default %USERPROFILE%\.minecraft-self-hoster\release-key.pem, or --key <path>).
// Never commit it, and keep a backup: without it, no update can be signed.

var key = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft-self-hoster", "release-key.pem");
var rest = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--key" && i + 1 < args.Length) key = args[++i];
    else rest.Add(args[i]);
}

switch (rest.FirstOrDefault())
{
    case "keygen":
        if (File.Exists(key)) { Console.Error.WriteLine($"A key already exists at {key}. Delete it first if you really want a new one (old releases' signatures won't match it)."); return 1; }
        Directory.CreateDirectory(Path.GetDirectoryName(key)!);
        using (var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            File.WriteAllText(key, ecdsa.ExportPkcs8PrivateKeyPem());
            Console.WriteLine($"Private key: {key}  (back it up somewhere safe; never share or commit it)");
            Console.WriteLine("Public key (put in Updater.PublicKey):");
            Console.WriteLine(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
        }
        return 0;

    case "sign" when rest.Count == 3:
        var exe = rest[1];
        var version = rest[2].TrimStart('v', 'V');
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(File.ReadAllText(key));
            var signature = ecdsa.SignData(Message(version, exe), HashAlgorithmName.SHA256);
            File.WriteAllText(exe + ".sig", Convert.ToBase64String(signature));
            Console.WriteLine($"Signed {Path.GetFileName(exe)} as version {version}: {exe}.sig");
        }
        return 0;

    case "verify" when rest.Count == 3:
        // checks <exe>.sig against the public key of the key file, the same check the app makes before installing
        using (var ecdsa = ECDsa.Create())
        {
            ecdsa.ImportFromPem(File.ReadAllText(key));
            var ok = ecdsa.VerifyData(Message(rest[2].TrimStart('v', 'V'), rest[1]), Convert.FromBase64String(File.ReadAllText(rest[1] + ".sig").Trim()), HashAlgorithmName.SHA256);
            Console.WriteLine(ok ? "Signature OK" : "Signature does NOT match");
            return ok ? 0 : 1;
        }

    default:
        Console.Error.WriteLine("usage: ReleaseSigner keygen | sign <exe> <version> | verify <exe> <version>  [--key <path>]");
        return 2;
}

// what's signed: the app's name, the version and the exe's SHA-256, so a signature can't be moved to another file or version
static byte[] Message(string version, string exe)
{
    using var stream = File.OpenRead(exe);
    var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    return Encoding.UTF8.GetBytes($"minecraft-self-hoster\n{version}\n{hash}");
}
