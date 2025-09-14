// ================= ZIPPYGO AUTOMATION TESTS =================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
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
            // Arrange & Act
            var repository = new SqlWabaPhoneRepository(_configuration);

            // Assert
            Assert.NotNull(repository);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ObterIdEstabelecimentoPorPhoneNumberIdAsync_WithInvalidPhoneNumberId_ShouldReturnNull(
            string phoneNumberId)
        {
            // Arrange
            var repository = new SqlWabaPhoneRepository(_configuration);

            // Act
            var result = await repository.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberId);

            // Assert
            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ObterPorPhoneNumberIdAsync_WithInvalidPhoneNumberId_ShouldReturnNull(
            string phoneNumberId)
        {
            // Arrange
            var repository = new SqlWabaPhoneRepository(_configuration);

            // Act
            var result = await repository.ObterPorPhoneNumberIdAsync(phoneNumberId);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task InserirOuAtualizarAsync_WithNullWabaPhone_ShouldReturnFalse()
        {
            // Arrange
            var repository = new SqlWabaPhoneRepository(_configuration);

            // Act
            var result = await repository.InserirOuAtualizarAsync(null);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public async Task InserirOuAtualizarAsync_WithEmptyPhoneNumberId_ShouldReturnFalse()
        {
            // Arrange
            var repository = new SqlWabaPhoneRepository(_configuration);
            var wabaPhone = new WabaPhone
            {
                PhoneNumberId = "",
                IdEstabelecimento = Guid.NewGuid(),
                Ativo = true
            };

            // Act
            var result = await repository.InserirOuAtualizarAsync(wabaPhone);

            // Assert
            Assert.False(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ExisteAtivoAsync_WithInvalidPhoneNumberId_ShouldReturnFalse(
            string phoneNumberId)
        {
            // Arrange
            var repository = new SqlWabaPhoneRepository(_configuration);

            // Act
            var result = await repository.ExisteAtivoAsync(phoneNumberId);

            // Assert
            Assert.False(result);
        }

        [Fact]
        public void WabaPhone_ShouldHaveCorrectProperties()
        {
            // Arrange
            var id = Guid.NewGuid();
            var phoneNumberId = "123456789";
            var idEstabelecimento = Guid.NewGuid();
            var descricao = "Teste";
            var dataCriacao = DateTime.UtcNow;
            var dataAtualizacao = DateTime.UtcNow;

            // Act
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

            // Assert
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
            // Act
            var wabaPhone = new WabaPhone();

            // Assert
            Assert.Equal(Guid.Empty, wabaPhone.Id);
            Assert.Equal(string.Empty, wabaPhone.PhoneNumberId); // Default is string.Empty, not null
            Assert.Equal(Guid.Empty, wabaPhone.IdEstabelecimento);
            Assert.True(wabaPhone.Ativo); // Default is true according to model
            Assert.Null(wabaPhone.Descricao);
        }
    }
}
// ================= ZIPPYGO AUTOMATION TESTS (END) =================