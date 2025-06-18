# 🚀 ZippyGo - API Backend (motoboyBackEnd)

Este é o back-end oficial da plataforma **ZippyGo**, desenvolvida para gerenciar entregas com motoboys de forma inteligente, rápida e organizada. A API oferece funcionalidades para pizzarias ou estabelecimentos acompanharem pedidos em tempo real, distribuírem rotas, monitorarem motoboys e integrarem-se com sistemas como iFood.

🔗 **[Acesse a documentação Swagger](https://zippy-api.onrender.com/swagger/index.html)**

---

## 📦 Tecnologias Utilizadas

- **.NET Core 6**
- **C#**
- **Entity Framework Core**
- **SQL Server**
- **API REST**
- **JWT Authentication**
- **Swagger**
- **AutoMapper**
- **Dapper (em alguns pontos)**
- **Hospedagem: Render.com**

---

## 🔑 Principais Funcionalidades

- 🔐 **Autenticação de usuários (JWT)**
- 📦 **Cadastro e gerenciamento de pedidos**
- 🧍‍♂️ **Cadastro e gestão de motoboys**
- 📍 **Acompanhamento de localização em tempo real (via latitude/longitude)**
- 🛵 **Atribuição automática/manual de pedidos**
- 🧾 **Relatórios de entrega**
- 🌐 **Integração com front-end e futuros apps mobile**

---

## 🛠️ Como rodar localmente

### Pré-requisitos:
- [.NET 6 SDK](https://dotnet.microsoft.com/download)
- SQL Server (local ou Docker)
- Visual Studio ou VS Code

### Passos:

```bash
# Clone o repositório
git clone https://github.com/farleyedu/motoboyBackEnd.git
cd motoboyBackEnd

# Crie o banco de dados
# Atualize a connection string no appsettings.json

# Rode as migrations (caso necessário)
dotnet ef database update

# Inicie a aplicação
dotnet run
```

A aplicação estará disponível em `https://localhost:5001` com Swagger em `/swagger`.

---

## 🔐 Ambiente de Produção

A API está em produção no Render:

👉 **[https://zippy-api.onrender.com/swagger/index.html](https://zippy-api.onrender.com/swagger/index.html)**

---

## 📂 Estrutura do Projeto

```
motoboyBackEnd/
│
├── Controllers/         # Endpoints HTTP
├── Services/            # Lógica de negócio
├── Models/              # Modelos das entidades
├── DTOs/                # Data Transfer Objects
├── Data/                # Contexto do banco e migrations
├── Utils/               # Helpers e configs
├── Program.cs           # Configuração principal
└── appsettings.json     # Configurações gerais
```

---

## ✅ Status

🟢 Projeto em produção com versão funcional. Em evolução contínua.

---

## 👨‍💻 Autor

**Farley Eduardo**  
📧 Farleysilvae@gmail.com  
🔗 [LinkedIn](https://www.linkedin.com/in/farley-eduardo-490913175)

---

## 📃 Licença

Este projeto está sob a licença MIT. Veja o arquivo `LICENSE` para mais detalhes.
