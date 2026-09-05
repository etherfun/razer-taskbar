// Port of the i18n test_lock() serialization: I18n language is global state,
// so the tests touching it must not run in parallel with anything else.

using Xunit;

namespace RazerTaskbar.Tests;

[CollectionDefinition("I18nSequential", DisableParallelization = true)]
public sealed class I18nSequentialCollection;
