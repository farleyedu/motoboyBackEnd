using APIBack.Service;
using Xunit;

namespace APIBack.Tests.Unit;
public class CommunicationEmojiRulesTests
{
    [Theory]
    [InlineData(null)] [InlineData("like")] [InlineData("👍🏽")] [InlineData("👨‍👩‍👧‍👦")] [InlineData("🇧🇷")]
    public void Aceita_emoji_completo_e_reacoes_legadas(string? emoji) => CommunicationEmojiRules.Validate(emoji);
    [Theory]
    [InlineData("")] [InlineData("olá 👍")] [InlineData("👍👍")] [InlineData("arquivo")]
    public void Rejeita_texto_e_varios_emojis(string emoji) => Assert.Throws<DeliveryDomainException>(() => CommunicationEmojiRules.Validate(emoji));
}
