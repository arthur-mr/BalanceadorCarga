const teste = require('node:test');
const verificar = require('node:assert/strict');
const fs = require('node:fs');
const caminho = require('node:path');
const vm = require('node:vm');

const pastaPainel = caminho.join(__dirname, '..', 'Balanceador', 'wwwroot');

function criarEstado() {
  return {
    placas: ['A', 'B'].map(id => ({
      id, nome: `ESP32-${id}`, online: true, habilitada: true, cpu: id === 'A' ? 25 : 55, erro: null, tarefas: []
    })),
    fila: [], ultimaDecisao: null, situacao: 'Nenhuma tarefa aguardando.', aviso: null,
    imagem: { sessao: 'imagem-1', quadro: 0, largura: 512, altura: 384, revisao: 0, blocosConcluidos: 0, totalBlocos: 3072, aviso: null }
  };
}

function lerTexto(elemento) {
  return (elemento.textContent || '') + ' ' + elemento.children.map(lerTexto).join(' ');
}

async function criarPainel({ falharEnvio = false, atrasarImagens = false } = {}) {
  const elementos = new Map();
  const requisicoes = [];
  const desenhos = [], imagensPendentes = [];
  let estado = criarEstado();
  let eventos;
  let numero = 0;
  function criarElemento(tipo) {
    return {
      tipo, children: [], atributos: {}, hidden: false, disabled: false,
      append(...filhos) { this.children.push(...filhos); },
      replaceChildren(...filhos) { this.children = filhos; },
      setAttribute(nome, valor) { this.atributos[nome] = valor; },
      getContext() { return {
        clearRect() {},
        createImageData(largura, altura) { return { data: new Uint8ClampedArray(largura * altura * 4) }; },
        putImageData(imagem) { desenhos.push(imagem.data); }
      }; }
    };
  }
  const html = fs.readFileSync(caminho.join(pastaPainel, 'index.html'), 'utf8');
  for (const identificador of html.matchAll(/id="([^"]+)"/g)) elementos.set(identificador[1], criarElemento('div'));
  const responder = dados => ({
    ok: true, status: 200, headers: { get() { return null; } }, async json() { return dados; }
  });
  const contexto = vm.createContext({
    document: {
      getElementById(id) {
        verificar.ok(elementos.has(id), `Elemento ausente: ${id}`);
        return elementos.get(id);
      },
      createElement: criarElemento
    },
    setTimeout() { return 0; }, clearTimeout() {},
    EventSource: class { constructor(endereco) { this.endereco = endereco; eventos = this; } },
    async fetch(endereco, opcoes) {
      requisicoes.push({ endereco, opcoes });
      if (endereco === '/api/info') return responder({
        modo: 'Simulator', duracaoSegundos: 20
      });
      if (endereco === '/api/painel/state') return responder(estado);
      if (endereco === '/api/painel/imagem/reset') {
        estado.imagem = { ...estado.imagem, sessao: 'imagem-2', revisao: 0, blocosConcluidos: 0 };
        return { ok: true, status: 204 };
      }
      if (endereco.startsWith('/api/painel/imagem?')) {
        const resposta = { ok: true, status: 200, headers: { get() { return '1'; } },
          async arrayBuffer() { return new Uint8Array(512 * 384 * 3).fill(64).buffer; } };
        return atrasarImagens ? new Promise(concluir => imagensPendentes.push(() => concluir(resposta))) : resposta;
      }
      if (endereco === '/api/tasks') {
        if (falharEnvio) return { ok: false, async json() { return { erro: 'Fila cheia.' }; } };
        numero++;
        estado.fila.push({ numero, nivel: JSON.parse(opcoes.body).nivel, estado: 'naFila', progresso: 0 });
        return responder({ numero });
      }
      if (endereco.includes('/participation')) {
        const id = endereco.split('/')[3];
        estado.placas.find(placa => placa.id === id).habilitada = JSON.parse(opcoes.body).habilitada;
        return { ok: true, status: 204 };
      }
      throw Error(`Rota inesperada: ${endereco}`);
    }
  });
  vm.runInContext(fs.readFileSync(caminho.join(pastaPainel, 'app.js'), 'utf8'), contexto);
  await new Promise(setImmediate);
  return {
    elementos, requisicoes, html, desenhos, imagensPendentes,
    atualizar(novo) { estado = novo; eventos.onmessage({ data: JSON.stringify(estado) }); },
    get eventos() { return eventos; }
  };
}

teste('painel apresenta tres botoes, duas placas e nenhum controle avancado', async () => {
  const painel = await criarPainel();
  verificar.equal(painel.elementos.get('placas').children.length, 2);
  for (const nivel of ['leve', 'medio', 'pesado']) verificar.ok(painel.elementos.has(`adicionar-${nivel}`));
  verificar.doesNotMatch(painel.html, /<select|<input|cenario-assimetrico|resolucao|limite-tarefas/);
  verificar.equal((painel.html.match(/<canvas/g) || []).length, 1);
  verificar.equal(painel.eventos.endereco, '/api/painel/events');
});

teste('cada botao envia apenas o nivel e a tarefa aparece na fila', async () => {
  const painel = await criarPainel();
  for (const nivel of ['leve', 'medio', 'pesado']) {
    await painel.elementos.get(`adicionar-${nivel}`).onclick();
    const envio = painel.requisicoes.filter(requisicao => requisicao.endereco === '/api/tasks').at(-1);
    verificar.deepEqual(JSON.parse(envio.opcoes.body), { nivel });
    verificar.equal(painel.elementos.get(`adicionar-${nivel}`).disabled, false);
  }
  verificar.equal(painel.elementos.get('tarefas-fila').children.length, 3);
  verificar.equal(painel.elementos.get('quantidade-fila').textContent, '3 tarefas');
});

teste('cartoes mostram CPU e tarefas por nivel com tempo restante aproximado', async () => {
  const painel = await criarPainel();
  const estado = criarEstado();
  estado.placas[0].tarefas = [
    { numero: 1, nivel: 'leve', estado: 'executando', progresso: 50 },
    { numero: 2, nivel: 'leve', estado: 'executando', progresso: 10 }
  ];
  painel.atualizar(estado);
  const texto = lerTexto(painel.elementos.get('placas').children[0]);
  verificar.match(texto, /25%/);
  verificar.match(texto, /#1/);
  verificar.match(texto, /#2/);
  verificar.match(texto, /Leve/);
  verificar.match(texto, /~10 s/);
});

teste('retirar e reincluir preserva a mesma instancia do botao de participacao', async () => {
  const painel = await criarPainel();
  const botaoInicial = painel.elementos.get('placas').children[0].children.find(filho => filho.tipo === 'button');
  await botaoInicial.onclick();
  verificar.deepEqual(JSON.parse(painel.requisicoes.find(requisicao => requisicao.endereco === '/api/boards/A/participation').opcoes.body), { habilitada: false });
  const botaoAposRetirar = painel.elementos.get('placas').children[0].children.find(filho => filho.tipo === 'button');
  verificar.equal(botaoAposRetirar, botaoInicial);
  verificar.equal(botaoAposRetirar.textContent, 'Reincluir placa');
  await botaoAposRetirar.onclick();
  const botaoAposReincluir = painel.elementos.get('placas').children[0].children.find(filho => filho.tipo === 'button');
  verificar.equal(botaoAposReincluir, botaoInicial);
  verificar.equal(botaoAposReincluir.textContent, 'Retirar placa');
});

teste('ultima decisao usa a explicacao do envio e nao compara a CPU atual novamente', async () => {
  const painel = await criarPainel();
  const estado = criarEstado();
  estado.placas[0].cpu = 80;
  estado.ultimaDecisao = { numero: 1, nivel: 'leve', placa: 'ESP32-A', explicacao: 'Tarefa #1 enviada para ESP32-A: estava com menor uso de CPU.' };
  painel.atualizar(estado);
  verificar.equal(painel.elementos.get('ultima-decisao').textContent, estado.ultimaDecisao.explicacao);
});

teste('CPU indisponivel nao e apresentada como zero', async () => {
  const painel = await criarPainel();
  const estado = criarEstado();
  estado.placas[0].cpu = null;
  estado.placas[0].online = false;
  painel.atualizar(estado);
  const texto = lerTexto(painel.elementos.get('placas').children[0]);
  verificar.match(texto, /Sem conex/);
  verificar.match(texto, /—/);
  verificar.doesNotMatch(texto, /0%/);
});

teste('erro ao adicionar tarefa e exibido sem deixar o botao bloqueado', async () => {
  const painel = await criarPainel({ falharEnvio: true });
  await painel.elementos.get('adicionar-leve').onclick();
  verificar.equal(painel.elementos.get('mensagem').textContent, 'Fila cheia.');
  verificar.equal(painel.elementos.get('mensagem').hidden, false);
  verificar.equal(painel.elementos.get('adicionar-leve').disabled, false);
});

teste('imagem avanca abaixo do painel e possui somente o botao de reset', async () => {
  const painel = await criarPainel();
  const estado = criarEstado();
  estado.imagem.revisao = 1;
  estado.imagem.blocosConcluidos = 1;
  painel.atualizar(estado);
  await new Promise(setImmediate);
  verificar.equal(painel.desenhos.length, 1);
  verificar.equal(painel.desenhos[0].length, 512 * 384 * 4);
  verificar.deepEqual(Array.from(painel.desenhos[0].slice(0, 4)), [128, 128, 128, 255]);
  verificar.equal(painel.elementos.get('barra-imagem').value, 1);
  verificar.ok(painel.elementos.has('resetar-imagem'));
  verificar.doesNotMatch(painel.html, /iniciar-imagem|parar-imagem|<select/);
});

teste('reset preserva tarefas e descarta imagem antiga recebida com atraso', async () => {
  const painel = await criarPainel({ atrasarImagens: true });
  const estado = criarEstado();
  estado.placas[0].tarefas = [{ numero: 1, nivel: 'leve', estado: 'executando', progresso: 25 }];
  estado.imagem.revisao = 1;
  estado.imagem.blocosConcluidos = 1;
  const eventoAnterior = JSON.parse(JSON.stringify(estado));
  painel.atualizar(estado);
  await new Promise(setImmediate);
  await painel.elementos.get('resetar-imagem').onclick();
  painel.imagensPendentes[0]();
  await new Promise(setImmediate);
  verificar.equal(painel.desenhos.length, 0);
  verificar.equal(painel.elementos.get('barra-imagem').value, 0);
  verificar.match(lerTexto(painel.elementos.get('placas').children[0]), /#1/);
  verificar.equal(painel.elementos.get('resetar-imagem').disabled, false);
  painel.atualizar(eventoAnterior);
  verificar.equal(painel.elementos.get('barra-imagem').value, 0);
});
