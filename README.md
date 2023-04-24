# animal-farm

## C#

Bindings are generated using the `pileks/uniffi-bindgen-cs` crate, version `0.2.1`. Code can be found [on this branch](https://github.com/pileks/uniffi-bindgen-cs/tree/pileks/v0.2.1).

To run the C# example, you need to have [.NET installed](https://dotnet.microsoft.com/en-us/download).

From the root directory, run the following:

```console
# Build the project, outputting a .so file
cargo build

# Install pileks_uniffi-bindgen-cs
cargo install pileks_uniffi-bindgen-cs

# Generate C# bindings
./generate_csharp_bindings.sh

# Change into the C# example directory
cd ./csharp

# Run the project
dotnet run
```