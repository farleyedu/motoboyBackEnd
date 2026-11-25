using System.Buffers.Binary;
using System.Text;

namespace APIBack.Social.Voz;

/// <summary>Duração de áudio sem decodificar (WAV pelo cabeçalho, MP3 contando os quadros). Formato desconhecido = null.</summary>
public static class AudioInfo
{
    private static readonly int[] BitrateMpeg1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    private static readonly int[] BitrateMpeg2L3 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
    private static readonly int[][] Taxas = { new[] { 11025, 12000, 8000 }, Array.Empty<int>(), new[] { 22050, 24000, 16000 }, new[] { 44100, 48000, 32000 } };

    public static decimal? Duracao(byte[] bytes, string mime) => mime switch
    {
        "audio/wav" or "audio/x-wav" or "audio/wave" => DuracaoWav(bytes),
        "audio/mpeg" or "audio/mp3" => DuracaoMp3(bytes),
        _ => DuracaoWav(bytes) ?? DuracaoMp3(bytes),
    };

    public static decimal? DuracaoWav(byte[] b)
    {
        if (b.Length < 44 || Encoding.ASCII.GetString(b, 0, 4) != "RIFF" || Encoding.ASCII.GetString(b, 8, 4) != "WAVE") return null;
        var pos = 12;
        int? taxaBytes = null;
        while (pos + 8 <= b.Length)
        {
            var id = Encoding.ASCII.GetString(b, pos, 4);
            var tam = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 4)), int.MaxValue);
            if (id == "fmt " && pos + 16 <= b.Length) taxaBytes = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 16));
            if (id == "data")
            {
                if (taxaBytes is not > 0) return null;
                var dados = Math.Min(tam, b.Length - pos - 8);
                return Math.Round((decimal)dados / taxaBytes.Value, 2);
            }
            pos += 8 + tam + (tam & 1);
        }
        return null;
    }

    public static decimal? DuracaoMp3(byte[] b)
    {
        var pos = 0;
        if (b.Length >= 10 && b[0] == 'I' && b[1] == 'D' && b[2] == '3')
            pos = 10 + ((b[6] & 0x7f) << 21 | (b[7] & 0x7f) << 14 | (b[8] & 0x7f) << 7 | (b[9] & 0x7f));
        long amostras = 0;
        var taxa = 0;
        var quadros = 0;
        while (pos + 4 <= b.Length)
        {
            if (b[pos] != 0xFF || (b[pos + 1] & 0xE0) != 0xE0) { pos++; continue; }
            var versao = (b[pos + 1] >> 3) & 3;      // 0 = 2.5, 2 = 2, 3 = 1
            var camada = (b[pos + 1] >> 1) & 3;      // 1 = Layer III
            var idxBit = (b[pos + 2] >> 4) & 0xF;
            var idxTaxa = (b[pos + 2] >> 2) & 3;
            var enchimento = (b[pos + 2] >> 1) & 1;
            if (versao == 1 || camada != 1 || idxBit is 0 or 15 || idxTaxa == 3) { pos++; continue; }
            var kbps = (versao == 3 ? BitrateMpeg1L3 : BitrateMpeg2L3)[idxBit];
            var sr = Taxas[versao][idxTaxa];
            var tamanho = (versao == 3 ? 144 : 72) * kbps * 1000 / sr + enchimento;
            if (tamanho < 4) { pos++; continue; }
            amostras += versao == 3 ? 1152 : 576;
            taxa = sr;
            quadros++;
            pos += tamanho;
        }
        return quadros == 0 || taxa == 0 ? null : Math.Round((decimal)amostras / taxa, 2);
    }

    /// <summary>WAV PCM 16 bits mono em silêncio (provedor falso).</summary>
    public static byte[] WavSilencio(decimal segundos, int taxa = 8000)
    {
        var amostras = (int)Math.Round(segundos * taxa);
        var dados = amostras * 2;
        var b = new byte[44 + dados];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(b, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), (uint)(36 + dados));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(b, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(24), (uint)taxa);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(28), (uint)(taxa * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(b, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(40), (uint)dados);
        return b;
    }
}

/// <summary>Tempo por palavra: do alinhamento por caractere (ElevenLabs) ou estimado pelo tamanho de cada palavra.</summary>
public static class Alinhamento
{
    public static IReadOnlyList<PalavraTempo> DeCaracteres(IReadOnlyList<string> caracteres, IReadOnlyList<decimal> inicios,
        IReadOnlyList<decimal> fins)
    {
        var n = Math.Min(caracteres.Count, Math.Min(inicios.Count, fins.Count));
        var palavras = new List<PalavraTempo>();
        var atual = new StringBuilder();
        decimal ini = 0, fim = 0;
        for (var i = 0; i < n; i++)
        {
            if (string.IsNullOrWhiteSpace(caracteres[i]))
            {
                Fechar();
                continue;
            }
            if (atual.Length == 0) ini = inicios[i];
            atual.Append(caracteres[i]);
            fim = fins[i];
        }
        Fechar();
        return palavras;

        void Fechar()
        {
            if (atual.Length == 0) return;
            palavras.Add(new PalavraTempo(atual.ToString(), Math.Round(ini, 2), Math.Round(Math.Max(fim, ini), 2)));
            atual.Clear();
        }
    }

    /// <summary>Divide a duração entre as palavras pelo número de letras (cada uma pesa pelo menos 2, para "e", "a").</summary>
    public static IReadOnlyList<PalavraTempo> Estimar(string texto, decimal duracao)
    {
        var palavras = texto.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (palavras.Length == 0 || duracao <= 0) return Array.Empty<PalavraTempo>();
        var pesos = palavras.Select(p => (decimal)Math.Max(2, p.Length)).ToArray();
        var total = pesos.Sum();
        var lista = new List<PalavraTempo>(palavras.Length);
        decimal t = 0;
        for (var i = 0; i < palavras.Length; i++)
        {
            var fim = i == palavras.Length - 1 ? duracao : t + duracao * pesos[i] / total;
            lista.Add(new PalavraTempo(palavras[i], Math.Round(t, 2), Math.Round(fim, 2)));
            t = fim;
        }
        return lista;
    }
}
