using System;
using System.Collections.Generic;
using APIBack.Model.Auth;
using APIBack.Service;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace APIBack.Tests.Unit
{
    public class JwtServiceTests
    {
        private readonly JwtService _service;

        public JwtServiceTests()
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:SecretKey"] = "super-secret-key-with-at-least-32-chars",
                    ["Jwt:Issuer"] = "ZippyGo",
                    ["Jwt:Audience"] = "ZippyGoAPI",
                    ["Jwt:ExpirationMinutes"] = "60",
                    ["Jwt:RefreshTokenExpirationDays"] = "7",
                })
                .Build();

            _service = new JwtService(configuration);
        }

        [Fact]
        public void GenerateToken_AndValidateToken_DevePreservarModulosAtivosDoEstabelecimento()
        {
            var estabelecimentoId = Guid.NewGuid();
            var payload = new JwtPayload
            {
                UserId = 7,
                Nome = "Farley",
                Email = "farley@email.com",
                IsSuperAdmin = true,
                EstabelecimentoId = estabelecimentoId,
                EstabelecimentoNome = "Amazon Nautica",
                TipoEstabelecimento = "nautica",
                EstabelecimentoModulosAtivos = new List<string> { "Usuarios", "WhatsApp", "Nautica" },
                Permissoes = new Dictionary<string, List<string>>
                {
                    ["Usuarios"] = new List<string> { "visualizar" }
                }
            };

            var token = _service.GenerateToken(payload);
            var validated = _service.ValidateToken(token);

            Assert.Equal(estabelecimentoId, validated.EstabelecimentoId);
            Assert.Contains("Usuarios", validated.EstabelecimentoModulosAtivos);
            Assert.Contains("WhatsApp", validated.EstabelecimentoModulosAtivos);
            Assert.Contains("Nautica", validated.EstabelecimentoModulosAtivos);
        }
    }
}
