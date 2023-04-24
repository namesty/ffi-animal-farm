#!/bin/bash
echo "Copying target/debug/libanimal_farm.so to csharp/uniffi.so"
cp ./target/debug/libanimal_farm.so ./csharp/uniffi.so
echo "Generating C# bindings..."
uniffi-bindgen-cs -o csharp/uniffi/ src/main.udl
echo "Bindings output to csharp/uniffi/"
