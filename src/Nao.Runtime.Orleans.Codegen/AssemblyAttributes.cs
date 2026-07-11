// This project triggers the Orleans C# source generator for grain interfaces
// defined in the F# Nao.Runtime.Orleans project.

using Nao.Runtime.Orleans.Grains;

[assembly: Orleans.GenerateCodeForDeclaringAssembly(typeof(ISessionGrain))]
