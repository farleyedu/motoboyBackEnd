namespace APIBack.Options;

/// <summary>Máquinas do Zippy Social (R14), seção <c>Social:Maquinas</c>.</summary>
public sealed class SocialMaquinasOptions
{
    public const string Secao = "Social:Maquinas";

    /// <summary>Máquinas por loja enquanto o plano (<c>pacotes.social_max_maquinas</c>) não existir.</summary>
    public int LimitePorLoja { get; set; } = 3;
}
