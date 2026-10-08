using APIBack.DTOs.Motoboy;
using Dapper;
using Npgsql;
using SkiaSharp;

namespace APIBack.Service;

public sealed class MotoboyContaService(NpgsqlDataSource source)
{
    private const string OwnMotoboy = "SELECT m.id FROM motoboy m JOIN usuario u ON u.id = m.id_usuario WHERE m.id_usuario = @UserId AND m.canonical_motoboy_id = m.id AND COALESCE(m.is_simulated, FALSE) = FALSE AND u.deleted_at IS NULL";

    public async Task<MotoboyContaDto?> GetAsync(int userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync<MotoboyContaDto>(new CommandDefinition($"""
            SELECT m.id AS MotoboyId, m.nome AS Nome, u.email AS Email, m.telefone AS Telefone,
                   m.cidade AS Cidade, m.uf AS Uf, m.modelo_moto AS ModeloMoto, m.placa_moto AS PlacaMoto,
                   m.ano_moto AS AnoMoto, m.status_cadastro AS StatusCadastro,
                   COALESCE('data:image/jpeg;base64,' || replace(encode(a.conteudo, 'base64'), E'\n', ''), m.avatar) AS Avatar
              FROM motoboy m JOIN usuario u ON u.id = m.id_usuario
              LEFT JOIN motoboy_documentos a ON a.motoboy_id = m.id AND a.tipo = 'avatar'
             WHERE m.id = ({OwnMotoboy});
            """, new { UserId = userId }, commandTimeout: 10, cancellationToken: ct));
        return row;
    }

    public async Task<bool> UpdateDadosAsync(int userId, MotoboyDadosRequest request, CancellationToken ct)
    {
        if (request.Nome.Trim().Length < 2) throw new ArgumentException("Informe seu nome completo.");
        await using var connection = await source.OpenConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        var id = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(OwnMotoboy + " FOR UPDATE OF m, u;", new { UserId = userId }, transaction, commandTimeout: 10, cancellationToken: ct));
        if (!id.HasValue) return false;
        var email = request.Email.Trim().ToLowerInvariant();
        var duplicate = await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT EXISTS(SELECT 1 FROM usuario WHERE LOWER(email) = @Email AND id <> @UserId AND deleted_at IS NULL)", new { Email = email, UserId = userId }, transaction, commandTimeout: 10, cancellationToken: ct));
        if (duplicate) throw new ArgumentException("Este e-mail já está cadastrado.");
        await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE motoboy SET nome = @Nome, telefone = @Telefone, cidade = @Cidade, uf = @Uf WHERE id = @Id;
            UPDATE usuario SET nome = @Nome, email = @Email, updated_at = NOW() WHERE id = @UserId;
            """, new { Id = id.Value, UserId = userId, Nome = request.Nome.Trim(), Email = email, Telefone = request.Telefone?.Trim(), Cidade = request.Cidade?.Trim(), Uf = request.Uf?.Trim().ToUpperInvariant() }, transaction, commandTimeout: 10, cancellationToken: ct));
        await transaction.CommitAsync(ct);
        return true;
    }

    public async Task<bool> UpdateVeiculoAsync(int userId, MotoboyVeiculoRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.ModeloMoto)) throw new ArgumentException("Informe o modelo da sua moto.");
        if (request.AnoMoto > DateTime.UtcNow.Year + 1) throw new ArgumentException("Confira o ano da sua moto.");
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition($"UPDATE motoboy SET modelo_moto = @Modelo, placa_moto = @Placa, ano_moto = @Ano, tipo_veiculo = 'moto' WHERE id = ({OwnMotoboy});", new { UserId = userId, Modelo = request.ModeloMoto.Trim(), Placa = request.PlacaMoto.ToUpperInvariant(), Ano = request.AnoMoto }, commandTimeout: 10, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyCollection<MotoboyDocumentoDto>> DocumentsAsync(int userId, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return (await connection.QueryAsync<MotoboyDocumentoDto>(new CommandDefinition($"SELECT id, tipo, enviado_em_utc AS EnviadoEmUtc, 'recebido' AS Status FROM motoboy_documentos WHERE motoboy_id = ({OwnMotoboy}) AND tipo <> 'avatar' ORDER BY tipo", new { UserId = userId }, commandTimeout: 10, cancellationToken: ct))).ToArray();
    }

    public async Task<MotoboyDocumentoDto?> SaveImageAsync(int userId, MotoboyImagemRequest request, CancellationToken ct)
    {
        var bytes = SanitizeImage(request.Base64, request.Tipo == "avatar");
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<MotoboyDocumentoDto>(new CommandDefinition($"""
            INSERT INTO motoboy_documentos (motoboy_id, tipo, conteudo)
            SELECT id, @Tipo, @Conteudo FROM ({OwnMotoboy}) own
            ON CONFLICT (motoboy_id, tipo) DO UPDATE SET conteudo = EXCLUDED.conteudo, enviado_em_utc = NOW(), id = gen_random_uuid()
            RETURNING id, tipo, enviado_em_utc AS EnviadoEmUtc, 'recebido' AS Status;
            """, new { UserId = userId, Tipo = request.Tipo, Conteudo = bytes }, commandTimeout: 10, cancellationToken: ct));
    }

    public async Task<byte[]?> ReadImageAsync(int userId, Guid id, CancellationToken ct)
    {
        await using var connection = await source.OpenConnectionAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<byte[]>(new CommandDefinition($"SELECT conteudo FROM motoboy_documentos WHERE id = @Id AND motoboy_id = ({OwnMotoboy})", new { UserId = userId, Id = id }, commandTimeout: 10, cancellationToken: ct));
    }

    internal static byte[] SanitizeImage(string base64, bool avatar)
    {
        if (string.IsNullOrWhiteSpace(base64)) throw new ArgumentException("Escolha uma foto JPG ou PNG.");
        if (base64.Length > 5_600_000) throw new ArgumentException("A imagem deve ter no máximo 4 MB.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { throw new ArgumentException("Imagem inválida. Escolha uma foto JPG ou PNG."); }
        if (bytes.Length > 4 * 1024 * 1024) throw new ArgumentException("A imagem deve ter no máximo 4 MB.");
        using var stream = new SKMemoryStream(bytes);
        using var codec = SKCodec.Create(stream);
        if (codec == null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png) || (long)codec.Info.Width * codec.Info.Height > 20_000_000)
            throw new ArgumentException("Escolha uma foto JPG ou PNG com até 20 megapixels.");
        using var bitmap = SKBitmap.Decode(codec);
        if (bitmap == null) throw new ArgumentException("Não foi possível ler essa imagem.");
        var limit = avatar ? 512 : 1800;
        var scale = Math.Min(1d, limit / (double)Math.Max(bitmap.Width, bitmap.Height));
        var origin=codec.EncodedOrigin;
        var swapped=origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        using var oriented=new SKBitmap(Math.Max(1,(int)((swapped?bitmap.Height:bitmap.Width)*scale)),Math.Max(1,(int)((swapped?bitmap.Width:bitmap.Height)*scale)));
        using(var canvas=new SKCanvas(oriented))
        {
            canvas.Clear(SKColors.White);
            // Corrige a orientação EXIF antes de removê-la na reencodificação.
            var (a,b,c,d,e,f)=origin switch {
                SKEncodedOrigin.TopRight => (-1f,0f,(float)bitmap.Width,0f,1f,0f),
                SKEncodedOrigin.BottomRight => (-1f,0f,(float)bitmap.Width,0f,-1f,(float)bitmap.Height),
                SKEncodedOrigin.BottomLeft => (1f,0f,0f,0f,-1f,(float)bitmap.Height),
                SKEncodedOrigin.LeftTop => (0f,1f,0f,1f,0f,0f),
                SKEncodedOrigin.RightTop => (0f,-1f,(float)bitmap.Height,1f,0f,0f),
                SKEncodedOrigin.RightBottom => (0f,-1f,(float)bitmap.Height,-1f,0f,(float)bitmap.Width),
                SKEncodedOrigin.LeftBottom => (0f,1f,0f,-1f,0f,(float)bitmap.Width),
                _ => (1f,0f,0f,0f,1f,0f)
            };
            var ratio=(float)scale;
            canvas.SetMatrix(new SKMatrix{ScaleX=a*ratio,SkewX=b*ratio,TransX=c*ratio,SkewY=d*ratio,ScaleY=e*ratio,TransY=f*ratio,Persp2=1});
            using var original=SKImage.FromBitmap(bitmap);
            canvas.DrawImage(original,0,0,new SKSamplingOptions(SKFilterMode.Linear),null);
        }
        using var image = SKImage.FromBitmap(oriented);
        using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 88);
        return encoded.ToArray(); // Reencodificação remove metadados e conteúdo alheio à imagem.
    }
}
