namespace OpenToDo.Plugins;

public interface IOpenToDoPlugin
{
    string Id { get; }
    string Name { get; }
    void Initialize();
}
