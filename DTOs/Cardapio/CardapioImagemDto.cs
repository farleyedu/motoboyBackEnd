namespace APIBack.DTOs.Cardapio
{
    /// <summary>URL publica da imagem recem-salva (produto ou categoria do cardapio).</summary>
    public class CardapioImagemDto
    {
        public string Url { get; set; } = string.Empty;
    }

    /// <summary>Link de uma imagem hospedada em outro lugar, para o servidor baixar e passar a hospedar.</summary>
    public class ImportarCardapioImagemRequest
    {
        public string Url { get; set; } = string.Empty;
    }
}
