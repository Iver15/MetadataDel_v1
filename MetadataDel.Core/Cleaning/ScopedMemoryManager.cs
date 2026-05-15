using System.Collections.Generic;

namespace MetadataDel.Core.Cleaning;

/// <summary>
/// Управляет временем жизни disposable объектов и гарантирует их корректное освобождение.
/// Помогает избежать утечек памяти при работе с ресурсами.
/// </summary>
public sealed class ScopedMemoryManager : IDisposable
{
    private readonly List<IDisposable> _disposables = new();
    private bool _disposed = false;

    /// <summary>
    /// Создает объект через фабрику и добавляет его в список для последующего освобождения.
    /// </summary>
    /// <typeparam name="T">Тип объекта, реализующий IDisposable</typeparam>
    /// <param name="factory">Фабричный метод для создания объекта</param>
    /// <returns>Созданный объект</returns>
    public T Create<T>(Func<T> factory) where T : class, IDisposable
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ScopedMemoryManager));

        var item = factory();
        ArgumentNullException.ThrowIfNull(item);
        _disposables.Add(item);
        return item;
    }

    /// <summary>
    /// Создает объект не реализующий IDisposable для удобства использования в одном стиле
    /// </summary>
    public T CreateNonDisposable<T>(Func<T> factory)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ScopedMemoryManager));

        return factory();
    }

    /// <summary>
    /// Освобождает все управляемые ресурсы.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        foreach (var disposable in _disposables)
        {
            try
            {
                disposable?.Dispose();
            }
            catch
            {
                // Игнорируем ошибки при освобождении ресурсов
            }
        }

        _disposables.Clear();
        _disposed = true;

        // Помогаем GC быстрее освободить память
        GC.Collect(0, GCCollectionMode.Optimized);
    }
}
