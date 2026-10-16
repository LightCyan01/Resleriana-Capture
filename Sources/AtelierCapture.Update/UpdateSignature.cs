using System.Security.Cryptography;

namespace AtelierCapture.Update;

internal static class UpdateSignature
{
    internal static void Verify(byte[] manifest, byte[] signature)
    {
        using var key = TrustedKey();
        if (
            signature.Length != 64
            || !key.VerifyData(
                manifest,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation
            )
        )
            throw new InvalidDataException("Update signature is missing or invalid.");
    }

    internal static void Sign(string manifestPath, string signaturePath, string keyPath)
    {
        var manifest = UpdateFiles.ReadSmall(manifestPath);
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(keyPath));
        using var trusted = TrustedKey();
        if (
            !key.ExportSubjectPublicKeyInfo()
                .AsSpan()
                .SequenceEqual(trusted.ExportSubjectPublicKeyInfo())
        )
            throw new InvalidDataException("Signing key does not match the updater's release key.");
        var signature = key.SignData(
            manifest,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );
        Verify(manifest, signature);
        File.WriteAllBytes(signaturePath, signature);
    }

    private static ECDsa TrustedKey()
    {
        using var resource =
            typeof(UpdateSignature).Assembly.GetManifestResourceStream(
                "AtelierCapture.Update.PublicKey"
            ) ?? throw new InvalidDataException("Updater release key is missing.");
        using var reader = new StreamReader(resource);
        var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(reader.ReadToEnd());
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }
}
