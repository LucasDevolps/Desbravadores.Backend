namespace Almirante.Api.Services;

/// <summary>Limites das colunas varchar e das listas do acompanhamento de classes e especialidades (issue #125).</summary>
public static class RegrasFormacao
{
    public const int NomeEspecialidadeMaximo = 150;
    public const int CodigoOficialMaximo = 20;
    public const int AreaMaximo = 100;
    public const int DescricaoMaximo = 2000;
    public const int PreRequisitosTextoMaximo = 1000;
    public const int ReferenciaMaximo = 300;
    public const int FonteMaximo = 300;
    public const int EdicaoMaximo = 50;
    public const int ObservacoesMaximo = 1000;
    public const int ObservacoesRevisaoMaximo = 2000;
    public const int JustificativaMaximo = EventoRegras.MotivoMaximo;
    public const int CodigoRequisitoMaximo = 20;
    public const int SecaoMaximo = 100;
    public const int OrigemHistoricaMaximo = 300;
    public const int CodigoValidacaoMaximo = 40;
    public const int AcaoMaximo = 40;

    public const int NivelDificuldadeMinimo = 1;
    public const int NivelDificuldadeMaximo = 3;
    public const int PreRequisitosMaximo = 50;
    public const int RequisitosPorRevisaoMaximo = 500;
    public const int ProfundidadeMaxima = 4;
    public const int EspecialidadesExigidasMaximo = 50;

    public const int PageSizePadrao = 20;
    public const int PageSizeMaximo = 100;
}
