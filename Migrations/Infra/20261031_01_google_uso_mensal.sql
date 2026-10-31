-- Contador mensal das chamadas pagas ao Google (hoje so o Geocoding). Cada linha = um servico em um mes (UTC, 'yyyy-MM').
-- O backend incrementa antes de chamar o Google e para ao chegar no limite mensal (Google:GeocodingLimiteMensal).
-- Se a tabela nao existir, o backend usa um contador diario em memoria e segue funcionando (ver GoogleUsoContador).
BEGIN;

CREATE TABLE IF NOT EXISTS google_uso_mensal (
    servico       text        NOT NULL,
    mes           char(7)     NOT NULL,
    total         integer     NOT NULL DEFAULT 0 CHECK (total >= 0),
    atualizado_em timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (servico, mes)
);

COMMIT;
