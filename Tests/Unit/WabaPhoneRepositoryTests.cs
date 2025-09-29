// ================= ZIPPYGO AUTOMATION TESTS =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Infra;
using APIBack.Automation.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class WabaPhoneRepositoryTests
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public WabaPhoneRepositoryTests()
        {
            _connectionString = "Host=localhost;Database=test;Username=test;Password=test";

            var configurationBuilder = new ConfigurationBuilder();
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                { "ConnectionStrings:DefaultConnection", _connectionString }
            });
            _configuration = configurationBuilder.Build();
        }

        [Fact]
        public void Constructor_ShouldInitializeWithConnectionString()
        {
            var repository = new SqlWabaPhoneRepository(_configuration);
            Assert.NotNull(repository);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ObterIdEstabelecimentoPorPhoneNumberIdAsync_WithInvalidPhoneNumberId_ShouldReturnNull(string phoneNumberId)
        {
            var repository = new SqlWabaPhoneRepository(_configuration);
            var result = await repository.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberId);
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ObterPorPhoneNumberIdAsync_WithInvalidPhoneNumberId_ShouldReturnNull(string phoneNumberId)
        {
            var repository = new SqlWabaPhoneRepository(_configuration);
            var result = await repository.ObterIdEstabelecimentoPorDisplayPhoneAsync(phoneNumberId);
            Assert.Null(result);
        }

        [Fact]
        public async Task InserirOuAtualizarAsync_WithNullWabaPhone_ShouldReturnFalse()
        {
            var repository = new SqlWabaPhoneRepository(_configuration);
            var result = await repository.InserirOuAtualizarAsync(null!);
            Assert.False(result);
        }

        [Fact]
        public async Task InserirOuAtualizarAsync_WithEmptyPhoneNumberId_ShouldReturnFalse()
        {
            var repository = new SqlWabaPhoneRepository(_configuration);
            var wabaPhone = new WabaPhone
            {
                PhoneNumberId = string.Empty,
                IdEstabelecimento = Guid.NewGuid(),
                Ativo = true
            };

            var result = await repository.InserirOuAtualizarAsync(wabaPhone);
            Assert.False(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ExisteAtivoAsync_WithInvalidPhoneNumberId_ShouldReturnFalse(string phoneNumberId)
        {
            var repository = new SqlWabaPhoneRepository(_configuration);
            var result = await repository.ExisteAtivoAsync(phoneNumberId);
            Assert.False(result);
        }

        [Fact]
        public void WabaPhone_ShouldHaveCorrectProperties()
        {
            var id = Guid.NewGuid();
            var phoneNumberId = "123456789";
            var idEstabelecimento = Guid.NewGuid();
            var descricao = "Teste";
            var dataCriacao = DateTime.UtcNow;
            var dataAtualizacao = DateTime.UtcNow;

            var wabaPhone = new WabaPhone
            {
                Id = id,
                PhoneNumberId = phoneNumberId,
                IdEstabelecimento = idEstabelecimento,
                Ativo = true,
                Descricao = descricao,
                DataCriacao = dataCriacao,
                DataAtualizacao = dataAtualizacao
            };

            Assert.Equal(id, wabaPhone.Id);
            Assert.Equal(phoneNumberId, wabaPhone.PhoneNumberId);
            Assert.Equal(idEstabelecimento, wabaPhone.IdEstabelecimento);
            Assert.True(wabaPhone.Ativo);
            Assert.Equal(descricao, wabaPhone.Descricao);
            Assert.Equal(dataCriacao, wabaPhone.DataCriacao);
            Assert.Equal(dataAtualizacao, wabaPhone.DataAtualizacao);
        }

        [Fact]
        public void WabaPhone_DefaultValues_ShouldBeCorrect()
        {
            var wabaPhone = new WabaPhone();

            Assert.Equal(Guid.Empty, wabaPhone.Id);
            Assert.Equal(string.Empty, wabaPhone.PhoneNumberId);
            Assert.Equal(Guid.Empty, wabaPhone.IdEstabelecimento);
            Assert.True(wabaPhone.Ativo);
            Assert.Null(wabaPhone.Descricao);
        }
    }
}
// ================= ZIPPYGO AUTOMATION TESTS (END) =================
