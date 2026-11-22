const elemento = identificador => document.getElementById(identificador);
const nomesNiveis = { leve: 'Leve', medio: 'Média', pesado: 'Pesada' };
const niveis = ['leve', 'medio', 'pesado'];
const limiteCpuAltaPercentual = 75;
const limiteSessoesAnteriores = 32;

let duracaoTarefaSegundos = 20;
let temporizadorMensagem;
let imagemAtual;
let sessaoDesenhada = '';
let revisaoDesenhada = -1;
let carregandoImagem = false;
const sessoesAnteriores = new Set();

function criarElemento(tipo, texto, classe) {
  const novo = document.createElement(tipo);
  if (texto !== undefined) {
    novo.textContent = texto;
  }
  if (classe) {
    novo.className = classe;
  }
  return novo;
}

function informar(texto) {
  const mensagem = elemento('mensagem');
  mensagem.textContent = texto;
  mensagem.hidden = false;

  clearTimeout(temporizadorMensagem);
  temporizadorMensagem = setTimeout(() => {
    mensagem.hidden = true;
  }, 5000);
}

async function requisitar(caminho, metodo = 'GET', corpo) {
  const resposta = await fetch(caminho, {
    method: metodo,
    headers: corpo ? { 'Content-Type': 'application/json' } : {},
    body: corpo ? JSON.stringify(corpo) : undefined
  });

  if (!resposta.ok) {
    const dados = await resposta.json().catch(() => null);
    throw Error(dados?.erro || 'Não foi possível falar com o balanceador.');
  }

  if (resposta.status === 204 || resposta.headers.get('content-length') === '0') {
    return null;
  }

  return resposta.json();
}

function obterSituacaoPlaca(placa) {
  if (!placa.habilitada) {
    return 'Retirada';
  }
  if (!placa.online) {
    return 'Sem conexão';
  }
  if (placa.cpu == null) {
    return 'Lendo CPU';
  }
  if (placa.tarefas.length > 0) {
    return 'Processando';
  }
  return 'Sem tarefas';
}

function renderizarTarefa(tarefa, emExecucao) {
  const item = criarElemento('li', undefined, 'tarefa');
  item.append(
    criarElemento('span', `#${tarefa.numero}`, 'numero'),
    criarElemento('span', nomesNiveis[tarefa.nivel], `nivel ${tarefa.nivel}`)
  );

  if (emExecucao) {
    const restante = Math.max(0, Math.ceil(duracaoTarefaSegundos * (1 - tarefa.progresso / 100)));
    const rotuloTempo = tarefa.estado === 'cancelando' ? 'Encerrando…' : `~${restante} s`;
    item.append(criarElemento('span', rotuloTempo, 'tempo'));
  }

  return item;
}

function renderizarPlaca(placa) {
  const cartao = criarElemento('article', undefined, 'placa');

  const titulo = criarElemento('div', undefined, 'titulo-secao');
  titulo.append(
    criarElemento('h2', placa.nome),
    criarElemento('span', obterSituacaoPlaca(placa), 'situacao')
  );
  cartao.append(titulo);

  const leitura = criarElemento('div', undefined, 'leitura-cpu');
  const valorCpu = placa.cpu == null ? '—' : `${Math.round(placa.cpu)}%`;
  leitura.append(criarElemento('span', 'Uso de CPU'), criarElemento('strong', valorCpu));

  const barra = criarElemento('progress');
  barra.max = 100;
  barra.value = placa.cpu ?? 0;
  barra.setAttribute('aria-label', `Uso de CPU de ${placa.nome}`);
  if (placa.cpu >= limiteCpuAltaPercentual) {
    barra.className = 'cpu-alta';
  }

  cartao.append(leitura, barra, criarElemento('h3', 'Tarefas em execução'));

  const tarefas = criarElemento('ul', undefined, 'tarefas');
  tarefas.append(...placa.tarefas.map(tarefa => renderizarTarefa(tarefa, true)));
  if (!placa.tarefas.length) {
    tarefas.append(criarElemento('li', 'Nenhuma tarefa em execução.', 'muted'));
  }
  cartao.append(tarefas);

  if (placa.erro) {
    const mensagem = placa.erro.includes('Protocolo incompatível')
      ? 'Atualize o firmware da placa para a versão 5.'
      : 'Aguardando comunicação com a placa.';
    cartao.append(criarElemento('p', mensagem, 'erro'));
  }

  const acao = placa.habilitada ? 'Retirar' : 'Reincluir';
  const participar = criarElemento('button', `${acao} placa`, 'secundario');
  participar.setAttribute('aria-label', `${acao} ${placa.nome}`);
  participar.onclick = async () => {
    participar.disabled = true;
    try {
      await requisitar(
        `/api/boards/${encodeURIComponent(placa.id)}/participation`,
        'PUT',
        { habilitada: !placa.habilitada }
      );
      renderizar(await requisitar('/api/painel/state'));
      elemento('mensagem').hidden = true;
    } catch (erro) {
      informar(erro.message);
    } finally {
      participar.disabled = false;
    }
  };
  cartao.append(participar);

  return cartao;
}

function renderizar(estado) {
  elemento('placas').replaceChildren(...estado.placas.map(renderizarPlaca));
  elemento('tarefas-fila').replaceChildren(...estado.fila.map(tarefa => renderizarTarefa(tarefa, false)));
  elemento('quantidade-fila').textContent = `${estado.fila.length} ${estado.fila.length === 1 ? 'tarefa' : 'tarefas'}`;
  elemento('situacao-fila').textContent = estado.situacao;
  elemento('ultima-decisao').textContent =
    estado.ultimaDecisao?.explicacao || 'Adicione uma tarefa para acompanhar a escolha.';

  const aviso = elemento('aviso');
  aviso.textContent = estado.aviso || '';
  aviso.hidden = !estado.aviso;

  renderizarImagem(estado.imagem);
}

function renderizarImagem(imagem) {
  if (!imagem || sessoesAnteriores.has(imagem.sessao)) {
    return;
  }

  if (imagemAtual && imagemAtual.sessao !== imagem.sessao) {
    sessoesAnteriores.add(imagemAtual.sessao);
    if (sessoesAnteriores.size > limiteSessoesAnteriores) {
      sessoesAnteriores.delete(sessoesAnteriores.values().next().value);
    }
  }

  imagemAtual = imagem;
  if (sessaoDesenhada !== imagem.sessao) {
    elemento('imagem-processada').getContext('2d').clearRect(0, 0, imagem.largura, imagem.altura);
    sessaoDesenhada = imagem.sessao;
    revisaoDesenhada = -1;
  }

  if (imagem.revisao === 0) {
    revisaoDesenhada = 0;
  }

  const percentual = 100 * imagem.blocosConcluidos / imagem.totalBlocos;
  elemento('progresso-imagem').textContent = `${percentual.toFixed(1)}% concluída`;
  elemento('barra-imagem').max = imagem.totalBlocos;
  elemento('barra-imagem').value = imagem.blocosConcluidos;

  const avisoImagem = elemento('aviso-imagem');
  avisoImagem.textContent = imagem.aviso || '';
  avisoImagem.hidden = !imagem.aviso;

  if (!carregandoImagem && imagem.revisao > revisaoDesenhada) {
    carregarImagem(imagem);
  }
}

function preencherImageDataComCorrecaoGama(pixelsRgb, imagemData) {
  const totalPixels = pixelsRgb.length / 3;
  for (let indice = 0; indice < totalPixels; indice++) {
    const origem = indice * 3;
    const destino = indice * 4;

    for (let canal = 0; canal < 3; canal++) {
      imagemData.data[destino + canal] = Math.round(255 * Math.sqrt(pixelsRgb[origem + canal] / 255));
    }
    imagemData.data[destino + 3] = 255;
  }
}

async function carregarImagem(imagem) {
  carregandoImagem = true;
  try {
    const resposta = await fetch(
      `/api/painel/imagem?sessao=${encodeURIComponent(imagem.sessao)}&revisao=${imagem.revisao}`,
      { cache: 'no-store' }
    );

    if (resposta.status === 404) {
      return;
    }

    if (!resposta.ok) {
      throw Error('Não foi possível atualizar a imagem. Uma nova tentativa será feita.');
    }

    const pixels = new Uint8Array(await resposta.arrayBuffer());
    if (imagemAtual?.sessao !== imagem.sessao) {
      return;
    }

    if (pixels.length !== imagem.largura * imagem.altura * 3) {
      throw Error('A imagem recebida está incompleta.');
    }

    const contexto = elemento('imagem-processada').getContext('2d');
    const desenho = contexto.createImageData(imagem.largura, imagem.altura);
    preencherImageDataComCorrecaoGama(pixels, desenho);
    contexto.putImageData(desenho, 0, 0);

    const revisao = Number(resposta.headers.get('X-Revisao'));
    revisaoDesenhada = Number.isInteger(revisao) ? revisao : imagem.revisao;
  } catch (erro) {
    if (imagemAtual?.sessao === imagem.sessao) {
      const avisoImagem = elemento('aviso-imagem');
      avisoImagem.textContent = erro.message;
      avisoImagem.hidden = false;
    }
  } finally {
    carregandoImagem = false;
  }
}

async function adicionarTarefa(nivel) {
  const botao = elemento(`adicionar-${nivel}`);
  botao.disabled = true;
  try {
    const trabalho = await requisitar('/api/tasks', 'POST', { nivel });
    renderizar(await requisitar('/api/painel/state'));
    informar(`Tarefa #${trabalho.numero} adicionada à fila.`);
  } catch (erro) {
    informar(erro.message);
  } finally {
    botao.disabled = false;
  }
}

for (const nivel of niveis) {
  elemento(`adicionar-${nivel}`).onclick = () => adicionarTarefa(nivel);
}

elemento('resetar-imagem').onclick = async () => {
  const botaoResetar = elemento('resetar-imagem');
  botaoResetar.disabled = true;
  try {
    await requisitar('/api/painel/imagem/reset', 'POST');
    renderizar(await requisitar('/api/painel/state'));
  } catch (erro) {
    informar(erro.message);
  } finally {
    botaoResetar.disabled = false;
  }
};

(async function iniciar() {
  try {
    const informacoes = await requisitar('/api/info');
    if (informacoes.protocolo !== 5 || informacoes.painel !== 'simples' || !informacoes.imagemProgressiva) {
      throw Error('Reinicie o balanceador atualizado para abrir o painel simples.');
    }

    if (Number.isFinite(informacoes.duracaoSegundos) && informacoes.duracaoSegundos > 0) {
      duracaoTarefaSegundos = informacoes.duracaoSegundos;
    }

    elemento('modo').textContent = informacoes.modo === 'Simulator'
      ? 'Simulação · CPU simulada'
      : 'Hardware · CPU medida';

    renderizar(await requisitar('/api/painel/state'));
    elemento('conexao').textContent = 'Conectado';

    const eventos = new EventSource('/api/painel/events');
    eventos.onmessage = evento => {
      renderizar(JSON.parse(evento.data));
      elemento('conexao').textContent = 'Conectado';
    };
    eventos.onerror = () => {
      elemento('conexao').textContent = 'Reconectando…';
    };
  } catch (erro) {
    informar(erro.message);
    elemento('conexao').textContent = 'Sem conexão';
    for (const nivel of niveis) {
      elemento(`adicionar-${nivel}`).disabled = true;
    }
  }
})();
