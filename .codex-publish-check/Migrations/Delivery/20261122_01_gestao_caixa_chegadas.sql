-- Gestao, 6C6: presença do motoboy na loja para o Caixa. Uma linha por loja e motoboy, gravada só quando o estado muda (o aviso ao
-- Caixa sai uma vez por transição, não a cada posição). Vem da posição que o rastreio já recebe ou do "cheguei" manual; não mexe na
-- rota (delivery_motoboy_route) nem em dinheiro. posicao_em guarda a hora de captura que decidiu: posição mais velha não volta atrás.
-- Aditiva e idempotente.
BEGIN;

CREATE TABLE IF NOT EXISTS gestao_caixa_chegadas (
    estabelecimento_id uuid NOT NULL,
    motoboy_id integer NOT NULL,
    estado varchar(10) NOT NULL CHECK (estado IN ('rota', 'chegando', 'provavel', 'loja')),
    origem varchar(10) NOT NULL CHECK (origem IN ('gps', 'manual')),
    desde timestamptz NOT NULL,
    posicao_em timestamptz,
    distancia_m integer,
    precisao_m double precision,
    atualizado_em timestamptz NOT NULL,
    PRIMARY KEY (estabelecimento_id, motoboy_id)
);

COMMIT;
