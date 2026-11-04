using DoraExplorer.App.Infrastructure;

namespace DoraExplorer.App.Services;

/// <summary>
/// Quantas leituras a procura de duplicados faz ao mesmo tempo. Medido (i7-11800H, dois NVMe, Defender ligado): em arquivos ainda não
/// lidos, 16 leituras em paralelo fizeram cerca de 1.860 arquivos/s contra cerca de 1.000 com 4, porque o custo está em abrir cada
/// arquivo (o antivírus examina na primeira abertura) e esse custo se paraleliza; o cálculo da hash quase não pesa. Num HD comum, porém,
/// muitas leituras ao mesmo tempo só fazem a cabeça de leitura pular de um lugar a outro.
/// </summary>
public static class DuplicateWorkers
{
    /// <summary>O que vale para HD, caminho de rede e disco desconhecido (o que a procura sempre usou).</summary>
    public const int Conservative = 4;

    /// <summary>O teto para SSD: acima disso o ganho medido some e só se gasta memória.</summary>
    public const int Ceiling = 16;

    /// <summary>
    /// SSD (sem cabeça de leitura): tantas leituras quantos processadores lógicos, de <see cref="Conservative"/> a <see cref="Ceiling"/>.
    /// HD (<paramref name="hasSeekPenalty"/> verdadeiro) e disco que não se conseguiu identificar (nulo): <see cref="Conservative"/>.
    /// </summary>
    public static int Choose(bool? hasSeekPenalty, int processorCount) =>
        hasSeekPenalty == false
            ? Math.Clamp(processorCount, Conservative, Ceiling)
            : Conservative;

    /// <summary>A escolha para a pasta dada, perguntando ao Windows que tipo de disco a guarda.</summary>
    public static int ForPath(string path) => Choose(StorageKind.HasSeekPenalty(path), Environment.ProcessorCount);
}
