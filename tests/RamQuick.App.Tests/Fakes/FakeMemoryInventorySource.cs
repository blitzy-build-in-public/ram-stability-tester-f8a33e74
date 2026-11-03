using System.Threading;
using RamQuick.Core;
using RamQuick.Sensors;

// Linked into RamQuick.TestHost and RamQuick.IntegrationTests as well: this file may depend on the
// BCL and the public RamQuick.Core and RamQuick.Sensors contracts only, never on a test framework,
// another fake, or an App, Interop or Logging type.
namespace RamQuick.App.Tests.Fakes;

/// <summary>
/// An <see cref="IMemoryInventorySource"/> whose inventory is scripted by the test, and which counts
/// how often it is read. It never queries the SMBIOS firmware table or any other hardware source.
/// </summary>
/// <remarks>
/// <para>
/// Every member is thread-safe: <c>RunController.InitializeAsync</c> reads the source on the thread
/// pool while the test scripts <see cref="Inventory"/> and <see cref="ReadException"/> and reads
/// <see cref="ReadCount"/> on its own thread.
/// </para>
/// <para>
/// Set <see cref="Inventory"/> to <see cref="MemoryInventory.Unknown"/>, or to a partially populated
/// inventory, to drive the detected-memory summary's "Unknown" fallbacks.
/// </para>
/// </remarks>
public sealed class FakeMemoryInventorySource : IMemoryInventorySource
{
    private MemoryInventory _inventory;
    private Exception? _readException;
    private int _readCount;

    /// <summary>
    /// Initializes a new instance that reports <paramref name="inventory"/>, or
    /// <see cref="DefaultInventory"/> when it is <see langword="null"/>.
    /// </summary>
    /// <param name="inventory">The inventory <see cref="Read"/> returns until a test sets another.</param>
    public FakeMemoryInventorySource(MemoryInventory? inventory = null)
    {
        _inventory = inventory ?? DefaultInventory;
    }

    /// <summary>
    /// Gets the inventory a new instance reports unless the constructor is given another: two DDR5
    /// DIMMs on two channels at 6000 MT/s, 32 GiB installed. The capacity matches the total
    /// physical memory of the fake engine's default system, so a default graph describes one
    /// consistent machine.
    /// </summary>
    public static MemoryInventory DefaultInventory { get; } = new(
        InstalledBytes: 32UL * 1024 * 1024 * 1024,
        DimmCount: 2,
        SpeedMTs: 6000,
        ChannelCount: 2,
        MemoryType: MemoryType.DDR5);

    /// <summary>
    /// Gets or sets the inventory the next <see cref="Read"/> returns.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value assigned is <see langword="null"/>.</exception>
    public MemoryInventory Inventory
    {
        get => Volatile.Read(ref _inventory);

        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Volatile.Write(ref _inventory, value);
        }
    }

    /// <summary>
    /// Gets the number of <see cref="Read"/> calls made on this instance, including calls that threw
    /// <see cref="ReadException"/>.
    /// </summary>
    public int ReadCount => Volatile.Read(ref _readCount);

    /// <summary>
    /// Gets or sets the exception <see cref="Read"/> throws instead of returning an inventory.
    /// Defaults to <see langword="null"/>, which makes <see cref="Read"/> return
    /// <see cref="Inventory"/>.
    /// </summary>
    /// <remarks>
    /// The <see cref="IMemoryInventorySource"/> contract never throws: the production source reports
    /// unreadable firmware data as <see cref="MemoryInventory.Unknown"/>. This member exists only to
    /// prove that callers tolerate a source that violates that contract.
    /// </remarks>
    public Exception? ReadException
    {
        get => Volatile.Read(ref _readException);
        set => Volatile.Write(ref _readException, value);
    }

    /// <summary>
    /// Counts the call, then throws <see cref="ReadException"/> when one is set, and otherwise
    /// returns the current <see cref="Inventory"/>.
    /// </summary>
    /// <returns>The current value of <see cref="Inventory"/>.</returns>
    /// <exception cref="Exception">The exception assigned to <see cref="ReadException"/>.</exception>
    public MemoryInventory Read()
    {
        Interlocked.Increment(ref _readCount);

        Exception? readException = Volatile.Read(ref _readException);
        if (readException is not null)
        {
            throw readException;
        }

        return Volatile.Read(ref _inventory);
    }
}
