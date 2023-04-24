

// Common helper code.
//
// Ideally this would live in a separate .cs file where it can be unittested etc
// in isolation, and perhaps even published as a re-useable package.
//
// However, it's important that the detils of how this helper code works (e.g. the
// way that different builtin types are passed across the FFI) exactly match what's
// expected by the Rust code on the other side of the interface. In practice right
// now that means coming from the exact some version of `uniffi` that was used to
// compile the Rust component. The easiest way to ensure this is to bundle the C#
// helpers directly inline like we're doing here.

using System.IO;
using System.Runtime.InteropServices;
using System;

namespace uniffi.main;



// This is a helper for safely working with byte buffers returned from the Rust code.
// A rust-owned buffer is represented by its capacity, its current length, and a
// pointer to the underlying data.

[StructLayout(LayoutKind.Sequential)]
internal struct RustBuffer {
    public int capacity;
    public int len;
    public IntPtr data;

    public static RustBuffer Alloc(int size) {
        return _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            var buffer = _UniFFILib.ffi_main_a7bc_rustbuffer_alloc(size, ref status);
            if (buffer.data == IntPtr.Zero) {
                throw new AllocationException($"RustBuffer.Alloc() returned null data pointer (size={size})");
            }
            return buffer;
        });
    }

    public static void Free(RustBuffer buffer) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_main_a7bc_rustbuffer_free(buffer, ref status);
        });
    }

    public BigEndianStream AsStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), len));
        }
    }

    public BigEndianStream AsWriteableStream() {
        unsafe {
            return new BigEndianStream(new UnmanagedMemoryStream((byte*)data.ToPointer(), capacity, capacity, FileAccess.Write));
        }
    }
}

// This is a helper for safely passing byte references into the rust code.
// It's not actually used at the moment, because there aren't many things that you
// can take a direct pointer to managed memory, and if we're going to copy something
// then we might as well copy it into a `RustBuffer`. But it's here for API
// completeness.

[StructLayout(LayoutKind.Sequential)]
internal struct ForeignBytes {
    public int length;
    public IntPtr data;
}


// The FfiConverter interface handles converter types to and from the FFI
//
// All implementing objects should be public to support external types.  When a
// type is external we need to import it's FfiConverter.
internal abstract class FfiConverter<CsType, FfiType> {
    // Convert an FFI type to a C# type
    public abstract CsType Lift(FfiType value);

    // Convert C# type to an FFI type
    public abstract FfiType Lower(CsType value);

    // Read a C# type from a `ByteBuffer`
    public abstract CsType Read(BigEndianStream stream);

    // Calculate bytes to allocate when creating a `RustBuffer`
    //
    // This must return at least as many bytes as the write() function will
    // write. It can return more bytes than needed, for example when writing
    // Strings we can't know the exact bytes needed until we the UTF-8
    // encoding, so we pessimistically allocate the largest size possible (3
    // bytes per codepoint).  Allocating extra bytes is not really a big deal
    // because the `RustBuffer` is short-lived.
    public abstract int AllocationSize(CsType value);

    // Write a C# type to a `ByteBuffer`
    public abstract void Write(CsType value, BigEndianStream stream);

    // Lower a value into a `RustBuffer`
    //
    // This method lowers a value into a `RustBuffer` rather than the normal
    // FfiType.  It's used by the callback interface code.  Callback interface
    // returns are always serialized into a `RustBuffer` regardless of their
    // normal FFI type.
    public RustBuffer LowerIntoRustBuffer(CsType value) {
        var rbuf = RustBuffer.Alloc(AllocationSize(value));
        try {
            var stream = rbuf.AsWriteableStream();
            Write(value, stream);
            rbuf.len = Convert.ToInt32(stream.Position);
            return rbuf;
        } catch {
            RustBuffer.Free(rbuf);
            throw;
        }
    }

    // Lift a value from a `RustBuffer`.
    //
    // This here mostly because of the symmetry with `lowerIntoRustBuffer()`.
    // It's currently only used by the `FfiConverterRustBuffer` class below.
    protected CsType LiftFromRustBuffer(RustBuffer rbuf) {
        var stream = rbuf.AsStream();
        try {
           var item = Read(stream);
           if (stream.HasRemaining()) {
               throw new InternalException("junk remaining in buffer after lifting, something is very wrong!!");
           }
           return item;
        } finally {
            RustBuffer.Free(rbuf);
        }
    }
}

// FfiConverter that uses `RustBuffer` as the FfiType
internal abstract class FfiConverterRustBuffer<CsType>: FfiConverter<CsType, RustBuffer> {
    public override CsType Lift(RustBuffer value) {
        return LiftFromRustBuffer(value);
    }
    public override RustBuffer Lower(CsType value) {
        return LowerIntoRustBuffer(value);
    }
}


// A handful of classes and functions to support the generated data structures.
// This would be a good candidate for isolating in its own ffi-support lib.
// Error runtime.
[StructLayout(LayoutKind.Sequential)]
struct RustCallStatus {
    public int code;
    public RustBuffer error_buf;

    public bool IsSuccess() {
        return code == 0;
    }

    public bool IsError() {
        return code == 1;
    }

    public bool IsPanic() {
        return code == 2;
    }
}

// Base class for all uniffi exceptions
public class UniffiException: Exception {
    public UniffiException(): base() {}
    public UniffiException(string message): base(message) {}
}

public class UndeclaredErrorException: UniffiException {
    public UndeclaredErrorException(string message): base(message) {}
}

public class PanicException: UniffiException {
    public PanicException(string message): base(message) {}
}

public class AllocationException: UniffiException {
    public AllocationException(string message): base(message) {}
}

public class InternalException: UniffiException {
    public InternalException(string message): base(message) {}
}

public class InvalidEnumException: InternalException {
    public InvalidEnumException(string message): base(message) {
    }
}

// Each top-level error class has a companion object that can lift the error from the call status's rust buffer
interface CallStatusErrorHandler<E> where E: Exception {
    E Lift(RustBuffer error_buf);
}

// CallStatusErrorHandler implementation for times when we don't expect a CALL_ERROR
class NullCallStatusErrorHandler: CallStatusErrorHandler<UniffiException> {
    public static NullCallStatusErrorHandler INSTANCE = new NullCallStatusErrorHandler();

    public UniffiException Lift(RustBuffer error_buf) {
        RustBuffer.Free(error_buf);
        return new UndeclaredErrorException("library has returned an error not declared in UNIFFI interface file");
    }
}

// Helpers for calling Rust
// In practice we usually need to be synchronized to call this safely, so it doesn't
// synchronize itself
class _UniffiHelpers {
    public delegate void RustCallAction(ref RustCallStatus status);
    public delegate U RustCallFunc<out U>(ref RustCallStatus status);

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static U RustCallWithError<U, E>(CallStatusErrorHandler<E> errorHandler, RustCallFunc<U> callback)
        where E: UniffiException
    {
        var status = new RustCallStatus();
        var return_value = callback(ref status);
        if (status.IsSuccess()) {
            return return_value;
        } else if (status.IsError()) {
            throw errorHandler.Lift(status.error_buf);
        } else if (status.IsPanic()) {
            // when the rust code sees a panic, it tries to construct a rustbuffer
            // with the message.  but if that code panics, then it just sends back
            // an empty buffer.
            if (status.error_buf.len > 0) {
                throw new PanicException(FfiConverterString.INSTANCE.Lift(status.error_buf));
            } else {
                throw new PanicException("Rust panic");
            }
        } else {
            throw new InternalException($"Unknown rust call status: {status.code}");
        }
    }

    // Call a rust function that returns a Result<>.  Pass in the Error class companion that corresponds to the Err
    public static void RustCallWithError<E>(CallStatusErrorHandler<E> errorHandler, RustCallAction callback)
        where E: UniffiException
    {
        _UniffiHelpers.RustCallWithError(errorHandler, (ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }

    // Call a rust function that returns a plain value
    public static U RustCall<U>(RustCallFunc<U> callback) {
        return _UniffiHelpers.RustCallWithError(NullCallStatusErrorHandler.INSTANCE, callback);
    }

    // Call a rust function that returns a plain value
    public static void RustCall(RustCallAction callback) {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            callback(ref status);
            return 0;
        });
    }
}


// Big endian streams are not yet available in dotnet :'(
// https://github.com/dotnet/runtime/issues/26904

class StreamUnderflowException: Exception {
    public StreamUnderflowException() {
    }
}

class BigEndianStream {
    Stream stream;
    public BigEndianStream(Stream stream) {
        this.stream = stream;
    }

    public bool HasRemaining() {
        return (stream.Length - stream.Position) > 0;
    }

    public long Position {
        get => stream.Position;
        set => stream.Position = value;
    }

    public void WriteBytes(byte[] value) {
        stream.Write(value, 0, value.Length);
    }

    public void WriteByte(byte value) {
        stream.WriteByte(value);
    }

    public void WriteUShort(ushort value) {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteUInt(uint value) {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    public void WriteULong(ulong value) {
        WriteUInt((uint)(value >> 32));
        WriteUInt((uint)value);
    }

    public void WriteSByte(sbyte value) {
        stream.WriteByte((byte)value);
    }

    public void WriteShort(short value) {
        WriteUShort((ushort)value);
    }

    public void WriteInt(int value) {
        WriteUInt((uint)value);
    }

    public void WriteFloat(float value) {
        WriteInt(BitConverter.SingleToInt32Bits(value));
    }

    public void WriteLong(long value) {
        WriteULong((ulong)value);
    }

    public void WriteDouble(double value) {
        WriteLong(BitConverter.DoubleToInt64Bits(value));
    }

    public byte[] ReadBytes(int length) {
        CheckRemaining(length);
        byte[] result = new byte[length];
        stream.Read(result, 0, length);
        return result;
    }

    public byte ReadByte() {
        CheckRemaining(1);
        return Convert.ToByte(stream.ReadByte());
    }

    public ushort ReadUShort() {
        CheckRemaining(2);
        return (ushort)(stream.ReadByte() << 8 | stream.ReadByte());
    }

    public uint ReadUInt() {
        CheckRemaining(4);
        return (uint)(stream.ReadByte() << 24
            | stream.ReadByte() << 16
            | stream.ReadByte() << 8
            | stream.ReadByte());
    }

    public ulong ReadULong() {
        return (ulong)ReadUInt() << 32 | (ulong)ReadUInt();
    }

    public sbyte ReadSByte() {
        return (sbyte)ReadByte();
    }

    public short ReadShort() {
        return (short)ReadUShort();
    }

    public int ReadInt() {
        return (int)ReadUInt();
    }

    public float ReadFloat() {
        return BitConverter.Int32BitsToSingle(ReadInt());
    }

    public long ReadLong() {
        return (long)ReadULong();
    }

    public double ReadDouble() {
        return BitConverter.Int64BitsToDouble(ReadLong());
    }

    private void CheckRemaining(int length) {
        if (stream.Length - stream.Position < length) {
            throw new StreamUnderflowException();
        }
    }
}

// Contains loading, initialization code,
// and the FFI Function declarations in a com.sun.jna.Library.


// This is an implementation detail which will be called internally by the public API.
static class _UniFFILib {
    static _UniFFILib() {
        
        FfiConverterTypeAnimal.INSTANCE.Register();
        }

    [DllImport("uniffi")]
    public static extern void ffi_main_a7bc_Farm_object_free(FarmSafeHandle @ptr,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern FarmSafeHandle main_a7bc_Farm_new(
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern void main_a7bc_Farm_add_animal(FarmSafeHandle @ptr,ulong @animal,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern void main_a7bc_Farm_remove_animal(FarmSafeHandle @ptr,RustBuffer @name,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern void ffi_main_a7bc_Animal_init_callback(ForeignCallback @callbackStub,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern void main_a7bc_add_animal(FarmSafeHandle @farm,ulong @animal,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern void main_a7bc_remove_animal(FarmSafeHandle @farm,RustBuffer @animalName,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern ulong main_a7bc_get_animal(FarmSafeHandle @farm,RustBuffer @animalName,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern FarmSafeHandle main_a7bc_create_farm(
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern void main_a7bc_native_speak(FarmSafeHandle @farm,RustBuffer @animalName,RustBuffer @message,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern RustBuffer ffi_main_a7bc_rustbuffer_alloc(int @size,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern RustBuffer ffi_main_a7bc_rustbuffer_from_bytes(ForeignBytes @bytes,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern void ffi_main_a7bc_rustbuffer_free(RustBuffer @buf,
    ref RustCallStatus _uniffi_out_err
    );

    [DllImport("uniffi")]
    public static extern RustBuffer ffi_main_a7bc_rustbuffer_reserve(RustBuffer @buf,int @additional,
    ref RustCallStatus _uniffi_out_err
    );

    
}

// Public interface members begin here.

#pragma warning disable 8625




class FfiConverterString: FfiConverter<string, RustBuffer> {
    public static FfiConverterString INSTANCE = new FfiConverterString();

    // Note: we don't inherit from FfiConverterRustBuffer, because we use a
    // special encoding when lowering/lifting.  We can use `RustBuffer.len` to
    // store our length and avoid writing it out to the buffer.
    public override string Lift(RustBuffer value) {
        try {
            var bytes = value.AsStream().ReadBytes(value.len);
            return System.Text.Encoding.UTF8.GetString(bytes);
        } finally {
            RustBuffer.Free(value);
        }
    }

    public override string Read(BigEndianStream stream) {
        var length = stream.ReadInt();
        var bytes = stream.ReadBytes(length);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    public override RustBuffer Lower(string value) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        var rbuf = RustBuffer.Alloc(bytes.Length);
        rbuf.AsWriteableStream().WriteBytes(bytes);
        return rbuf;
    }

    // TODO(CS)
    // We aren't sure exactly how many bytes our string will be once it's UTF-8
    // encoded.  Allocate 3 bytes per unicode codepoint which will always be
    // enough.
    public override int AllocationSize(string value) {
        const int sizeForLength = 4;
        var sizeForString = value.Length * 3;
        return sizeForLength + sizeForString;
    }

    public override void Write(string value, BigEndianStream stream) {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        stream.WriteInt(bytes.Length);
        stream.WriteBytes(bytes);
    }
}




// `SafeHandle` implements the semantics outlined below, i.e. its thread safe, and the dispose
// method will only be called once, once all outstanding native calls have completed.
// https://github.com/mozilla/uniffi-rs/blob/0dc031132d9493ca812c3af6e7dd60ad2ea95bf0/uniffi_bindgen/src/bindings/kotlin/templates/ObjectRuntime.kt#L31
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.criticalhandle

public abstract class FFIObject<THandle>: IDisposable where THandle : FFISafeHandle {
    private THandle handle;

    public FFIObject(THandle handle) {
        this.handle = handle;
    }

    public THandle GetHandle() {
        return handle;
    }

    public void Dispose() {
        handle.Dispose();
    }
}

public abstract class FFISafeHandle: SafeHandle {
    public FFISafeHandle(): base(new IntPtr(0), true) {
    }

    public FFISafeHandle(IntPtr pointer): this() {
        this.SetHandle(pointer);
    }

    public override bool IsInvalid {
        get {
            return handle.ToInt64() == 0;
        }
    }

    // TODO(CS) this completely breaks any guarantees offered by SafeHandle.. Extracting
    // raw value from SafeHandle puts responsiblity on the consumer of this function to
    // ensure that SafeHandle outlives the stream, and anyone who might have read the raw
    // value from the stream and are holding onto it. Otherwise, the result might be a use
    // after free, or free while method calls are still in flight.
    //
    // This is also relevant for Kotlin.
    //
    public IntPtr DangerousGetRawFfiValue() {
        return handle;
    }
}

static class FFIObjectUtil {
    public static void DisposeAll(params Object?[] list) {
        foreach (var obj in list) {
            Dispose(obj);
        }
    }

    // Dispose is implemented by recursive type inspection at runtime. This is because
    // generating correct Dispose calls for recursive complex types, e.g. List<List<int>>
    // is quite cumbersome.
    private static void Dispose(dynamic? obj) {
        if (obj == null) {
            return;
        }

        if (obj is IDisposable disposable) {
            disposable.Dispose();
            return;
        }

        var type = obj.GetType();
        if (type != null) {
            if (type.IsGenericType) {
                if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(List<>))) {
                    foreach (var value in obj) {
                        Dispose(value);
                    }
                } else if (type.GetGenericTypeDefinition().IsAssignableFrom(typeof(Dictionary<,>))) {
                    foreach (var value in obj.Values) {
                        Dispose(value);
                    }
                }
            }
        }
    }
}

public interface IFarm {
    
    void AddAnimal(Animal @animal);
    
    void RemoveAnimal(String @name);
    
}

public class FarmSafeHandle: FFISafeHandle {
    public FarmSafeHandle(): base() {
    }
    public FarmSafeHandle(IntPtr pointer): base(pointer) {
    }
    override protected bool ReleaseHandle() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_main_a7bc_Farm_object_free(this, ref status);
        });
        return true;
    }
}

public class Farm: FFIObject<FarmSafeHandle>, IFarm {
    public Farm(FarmSafeHandle pointer): base(pointer) {}
    public Farm() :
        this(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_Farm_new( ref _status)
)) {}

    
    public void AddAnimal(Animal @animal) {
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_Farm_add_animal(this.GetHandle(), FfiConverterTypeAnimal.INSTANCE.Lower(@animal), ref _status)
);
    }
    
    
    public void RemoveAnimal(String @name) {
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_Farm_remove_animal(this.GetHandle(), FfiConverterString.INSTANCE.Lower(@name), ref _status)
);
    }
    
    

    
}

class FfiConverterTypeFarm: FfiConverter<Farm, FarmSafeHandle> {
    public static FfiConverterTypeFarm INSTANCE = new FfiConverterTypeFarm();

    public override FarmSafeHandle Lower(Farm value) {
        return value.GetHandle();
    }

    public override Farm Lift(FarmSafeHandle value) {
        return new Farm(value);
    }

    public override Farm Read(BigEndianStream stream) {
        return Lift(new FarmSafeHandle(new IntPtr(stream.ReadLong())));
    }

    public override int AllocationSize(Farm value) {
        return 8;
    }

    public override void Write(Farm value, BigEndianStream stream) {
        stream.WriteLong(Lower(value).DangerousGetRawFfiValue().ToInt64());
    }
}






class ConcurrentHandleMap<T> where T: notnull {
    Dictionary<ulong, T> leftMap = new Dictionary<ulong, T>();
    Dictionary<T, ulong> rightMap = new Dictionary<T, ulong>();

    Object lock_ = new Object();
    ulong currentHandle = 0;

    public ulong Insert(T obj) {
        lock (lock_) {
            ulong existingHandle = 0;
            if (rightMap.TryGetValue(obj, out existingHandle)) {
                return existingHandle;
            }
            currentHandle += 1;
            leftMap[currentHandle] = obj;
            rightMap[obj] = currentHandle;
            return currentHandle;
        }
    }

    public bool TryGet(ulong handle, out T result) {
        // Possible null reference assignment
        #pragma warning disable 8601
        return leftMap.TryGetValue(handle, out result);
        #pragma warning restore 8601
    }

    public bool Remove(ulong handle) {
        return Remove(handle, out T result);
    }

    public bool Remove(ulong handle, out T result) {
        lock (lock_) {
            // Possible null reference assignment
            #pragma warning disable 8601
            if (leftMap.Remove(handle, out result)) {
            #pragma warning restore 8601
                rightMap.Remove(result);
                return true;
            } else {
                return false;
            }
        }
    }
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int ForeignCallback(ulong handle, int method, RustBuffer args, ref RustBuffer outBuf);

internal abstract class FfiConverterCallbackInterface<CallbackInterface>
        : FfiConverter<CallbackInterface, ulong>
        where CallbackInterface: notnull
{
    ConcurrentHandleMap<CallbackInterface> handleMap = new ConcurrentHandleMap<CallbackInterface>();

    // Registers the foreign callback with the Rust side.
    // This method is generated for each callback interface.
    public abstract void Register();

    public RustBuffer Drop(ulong handle) {
        handleMap.Remove(handle);
        return new RustBuffer();
    }

    public override CallbackInterface Lift(ulong handle) {
        if (!handleMap.TryGet(handle, out CallbackInterface result)) {
            throw new InternalException($"No callback in handlemap '{handle}'");
        }
        return result;
    }

    public override CallbackInterface Read(BigEndianStream stream) {
        return Lift(stream.ReadULong());
    }

    public override ulong Lower(CallbackInterface value) {
        return handleMap.Insert(value);
    }

    public override int AllocationSize(CallbackInterface value) {
        return 8;
    }

    public override void Write(CallbackInterface value, BigEndianStream stream) {
        stream.WriteULong(Lower(value));
    }
}

// Declaration and FfiConverters for Animal Callback Interface
public interface Animal {
    String GetName();
    String Speak(String @msg);
}

// The ForeignCallback that is passed to Rust.
class ForeignCallbackTypeAnimal {
    // This cannot be a static method. Although C# supports implicitly using a static method as a
    // delegate, the behaviour is incorrect for this use case. Using static method as a delegate
    // argument creates an implicit delegate object, that is later going to be collected by GC. Any
    // attempt to invoke a garbage collected delegate results in an error:
    //   > A callback was made on a garbage collected delegate of type 'ForeignCallback::..'
    public static ForeignCallback INSTANCE = (ulong handle, int method, RustBuffer args, ref RustBuffer outBuf) => {
        var cb = FfiConverterTypeAnimal.INSTANCE.Lift(handle);
        switch (method) {
            case 0: {
                // 0 means Rust is done with the callback, and the callback
                // can be dropped by the foreign language.
                FfiConverterTypeAnimal.INSTANCE.Drop(handle);
                // No return value.
                // See docs of ForeignCallback in `uniffi/src/ffi/foreigncallbacks.rs`
                return 0;
            }

            
            case 1: {
                try {
                    outBuf = InvokeGetName(cb, args);
                    return 1;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            
            case 2: {
                try {
                    outBuf = InvokeSpeak(cb, args);
                    return 1;
                } catch (Exception e) {
                    // Unexpected error
                    try {
                        // Try to serialize the error into a string
                        outBuf = FfiConverterString.INSTANCE.Lower(e.Message);
                    } catch {
                        // If that fails, then it's time to give up and just return
                    }
                    return -1;
                }
            }

            
            default: {
                // This should never happen, because an out of bounds method index won't
                // ever be used. Once we can catch errors, we should return an InternalException.
                // https://github.com/mozilla/uniffi-rs/issues/351
                return -1;
            }
        }
    };

    static RustBuffer InvokeGetName(Animal callback, RustBuffer args) {
        try {
            var result =callback.GetName();

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return FfiConverterString.INSTANCE.LowerIntoRustBuffer(result);
        } finally {
            RustBuffer.Free(args);
        }
    }

    static RustBuffer InvokeSpeak(Animal callback, RustBuffer args) {
        try {
            var stream = args.AsStream();
            var result =callback.Speak(FfiConverterString.INSTANCE.Read(stream));

            // TODO catch errors and report them back to Rust.
            // https://github.com/mozilla/uniffi-rs/issues/351
            return FfiConverterString.INSTANCE.LowerIntoRustBuffer(result);
        } finally {
            RustBuffer.Free(args);
        }
    }

    
}

// The ffiConverter which transforms the Callbacks in to Handles to pass to Rust.
class FfiConverterTypeAnimal: FfiConverterCallbackInterface<Animal> {
    public static FfiConverterTypeAnimal INSTANCE = new FfiConverterTypeAnimal();

    public override void Register() {
        _UniffiHelpers.RustCall((ref RustCallStatus status) => {
            _UniFFILib.ffi_main_a7bc_Animal_init_callback(ForeignCallbackTypeAnimal.INSTANCE, ref status);
        });
    }
}
#pragma warning restore 8625

public static class MainMethods {
    public static void AddAnimal(Farm @farm, Animal @animal) {
        
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_add_animal(FfiConverterTypeFarm.INSTANCE.Lower(@farm), FfiConverterTypeAnimal.INSTANCE.Lower(@animal), ref _status)
);
    }

    public static void RemoveAnimal(Farm @farm, String @animalName) {
        
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_remove_animal(FfiConverterTypeFarm.INSTANCE.Lower(@farm), FfiConverterString.INSTANCE.Lower(@animalName), ref _status)
);
    }

    public static Animal GetAnimal(Farm @farm, String @animalName) {
        return FfiConverterTypeAnimal.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_get_animal(FfiConverterTypeFarm.INSTANCE.Lower(@farm), FfiConverterString.INSTANCE.Lower(@animalName), ref _status)
));
    }

    public static Farm CreateFarm() {
        return FfiConverterTypeFarm.INSTANCE.Lift(
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_create_farm( ref _status)
));
    }

    public static void NativeSpeak(Farm @farm, String @animalName, String @message) {
        
    _UniffiHelpers.RustCall( (ref RustCallStatus _status) =>
    _UniFFILib.main_a7bc_native_speak(FfiConverterTypeFarm.INSTANCE.Lower(@farm), FfiConverterString.INSTANCE.Lower(@animalName), FfiConverterString.INSTANCE.Lower(@message), ref _status)
);
    }

}

