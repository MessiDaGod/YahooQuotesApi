﻿using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace YahooQuotesApi.Tests
{
    public class SymbolTest : TestBase
    {
        public SymbolTest(ITestOutputHelper output) : base(output) { }

        [Fact]
        public void TestArgs()
        {
#pragma warning disable CS8625 // Cannot convert null literal to non-nullable reference type.
            Assert.Throws<ArgumentNullException>(() => ((string?)null).ToSymbol());
#pragma warning restore CS8625 // Cannot convert null literal to non-nullable reference type.

            Assert.Throws<ArgumentException>(() => "with space".ToSymbol());

            Assert.Throws<ArgumentException>(() => "".ToSymbol());
            var empty = Symbol.Empty;
            Assert.True(empty.IsEmpty && !empty.IsStock && !empty.IsCurrency && !empty.IsCurrencyRate);

            var stock = "ABC".ToSymbol();
            Assert.True(!stock.IsEmpty && stock.IsStock && !stock.IsCurrency && !stock.IsCurrencyRate);

            var currency = "ABC=X".ToSymbol();
            Assert.True(!currency.IsEmpty && !currency.IsStock && currency.IsCurrency && !currency.IsCurrencyRate);
            Assert.Equal("ABC", currency.Currency);

            var rate = "ABCDEF=X".ToSymbol();
            Assert.True(!rate.IsEmpty && !rate.IsStock && !rate.IsCurrency && rate.IsCurrencyRate);
            Assert.Equal("DEF", rate.Currency);

            Assert.Throws<ArgumentException>(() => "ABCABC=X".ToSymbol());
            Assert.Throws<ArgumentException>(() => "ABCBC=X".ToSymbol());
            Assert.Throws<ArgumentException>(() => "=XABC".ToSymbol());
        }

        [Fact]
        public void TestOperators()
        {
            Assert.True("abc".ToSymbol() == "abc".ToSymbol());
            Assert.Equal("abc".ToSymbol(), "abc".ToSymbol());
            Assert.NotEqual("abc".ToSymbol(), "def".ToSymbol());

            var list = new List<Symbol>
            {
                "c5".ToSymbol(),
                "c2".ToSymbol(),
                "c1".ToSymbol(),
                "c4".ToSymbol()
            };

            list.Sort();
            Assert.Equal("c1".ToSymbol(), list.First());

            var dict = list.ToDictionary(x => x, x => x.Name);
            var result = dict["c2".ToSymbol()];
            Assert.Equal("C2", result);
        }

        [Fact]
        public void TestDefaultEquality()
        {
            var defaultSymbol = Symbol.TryCreate("", true);
            Assert.True(defaultSymbol?.IsEmpty);
            Assert.Equal(Symbol.Empty, defaultSymbol);
        }


        [Theory]
        [InlineData("A", "")]
        [InlineData(".", "")]
        [InlineData("A.", "")]
        [InlineData(".A", "A")]
        [InlineData("A.B", "B")]
        [InlineData("ABC.DEF", "DEF")]
        public void TestSuffix(string symbolName, string suffix)
        {
            var symbol = Symbol.TryCreate(symbolName) ?? throw new Exception(symbolName);
            Assert.Equal(suffix, symbol.Suffix);
        }
    }
}
