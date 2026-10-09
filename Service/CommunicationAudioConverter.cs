using System.Diagnostics;

namespace APIBack.Service;

// Formato comum para mensagens do painel (WebM/Opus) e do celular (AAC/M4A).
public sealed class CommunicationAudioConverter(IConfiguration configuration)
{
    private readonly SemaphoreSlim slots = new(2);
    public static bool NeedsConversion(string type) => type is "audio/webm" or "audio/ogg" or "audio/wav";

    public async Task<byte[]> ConvertAsync(byte[] content, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        await slots.WaitAsync(timeout.Token);
        var folder = Path.Combine(Path.GetTempPath(), "zippy-chat-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var source = Path.Combine(folder, "source");
            var output = Path.Combine(folder, "voice.m4a");
            await File.WriteAllBytesAsync(source, content, timeout.Token);
            var start = new ProcessStartInfo(configuration["Communication:FFmpegPath"] ?? "ffmpeg")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
            };
            foreach (var arg in new[] { "-nostdin", "-hide_banner", "-loglevel", "error", "-protocol_whitelist", "file,pipe", "-i", source, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "44100", "-c:a", "aac", "-b:a", "80k", "-fs", "10485761", "-movflags", "+faststart", "-y", output }) start.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = start };
            try { process.Start(); }
            catch (System.ComponentModel.Win32Exception) { throw new DeliveryDomainException(503, "CHAT_AUDIO_PROCESSOR_UNAVAILABLE", "O processamento de áudio está indisponível. Tente novamente em instantes."); }
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
                throw;
            }
            await Task.WhenAll(stderr, stdout);
            if (process.ExitCode != 0 || !File.Exists(output)) throw new DeliveryDomainException(422, "CHAT_AUDIO_INVALID", "Não foi possível abrir este áudio. Grave novamente.");
            var result = await File.ReadAllBytesAsync(output, timeout.Token);
            if (result.Length is < 12 or > 10485760) throw new DeliveryDomainException(422, "CHAT_AUDIO_INVALID", "Este áudio não é válido.");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new DeliveryDomainException(503, "CHAT_AUDIO_BUSY", "O processamento do áudio demorou. Tente novamente."); }
        finally
        {
            // Somente os arquivos fixos que este método criou; não há remoção recursiva.
            foreach (var file in new[] { "source", "voice.m4a" })
                try { File.Delete(Path.Combine(folder, file)); } catch (IOException) { }
            try { if (Directory.Exists(folder)) Directory.Delete(folder); } catch (IOException) { }
            slots.Release();
        }
    }
}
