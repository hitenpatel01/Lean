﻿/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
*/

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using QLNet;
using QuantConnect.Util;

namespace QuantConnect.Securities.Option.StrategyMatcher
{
    /// <summary>
    /// Provides indexing of option contracts
    /// </summary>
    public class OptionPositionCollection : IEnumerable<OptionPosition>
    {
        /// <summary>
        /// Gets an empty instance of <see cref="OptionPositionCollection"/>
        /// </summary>
        public static OptionPositionCollection Empty { get; } = new OptionPositionCollection(
            ImmutableDictionary<Symbol, OptionPosition>.Empty,
            ImmutableDictionary<OptionRight, ImmutableHashSet<Symbol>>.Empty,
            ImmutableSortedDictionary<decimal, ImmutableHashSet<Symbol>>.Empty,
            ImmutableSortedDictionary<DateTime, ImmutableHashSet<Symbol>>.Empty
        );

        /// <summary>
        /// Gets the underlying security's symbol
        /// </summary>
        public Symbol Underlying => UnderlyingPosition.Symbol ?? Symbol.Empty;

        /// <summary>
        /// Gets the total count of unique positions, including the underlying
        /// </summary>
        public int Count => _positions.Count;

        /// <summary>
        /// Gets whether or not there's any positions in this collection.
        /// </summary>
        public bool IsEmpty => UnderlyingPosition.Symbol == null;

        /// <summary>
        /// Gets the quantity of underlying shares held
        /// </summary>
        public int UnderlyingQuantity => UnderlyingPosition.Quantity;

        /// <summary>
        /// Gets the number of unique put contracts held (long or short)
        /// </summary>
        public int UniquePuts => _rights[OptionRight.Put].Count;

        /// <summary>
        /// Gets the unique number of expirations
        /// </summary>
        public int UniqueExpirations => _expirations.Count;

        /// <summary>
        /// Gets the number of unique call contracts held (long or short)
        /// </summary>
        public int UniqueCalls => _rights[OptionRight.Call].Count;

        /// <summary>
        /// Determines if this collection contains a position in the underlying
        /// </summary>
        public bool HasUnderlying => UnderlyingQuantity != 0;

        /// <summary>
        /// Gets the <see cref="Underlying"/> position
        /// </summary>
        public OptionPosition UnderlyingPosition { get; }

        /// <summary>
        /// Gets all unique strike prices in the collection, in ascending order.
        /// </summary>
        public IEnumerable<decimal> Strikes => _strikes.Keys;

        /// <summary>
        /// Gets all unique expiration dates in the collection, in chronological order.
        /// </summary>
        public IEnumerable<DateTime> Expirations => _expirations.Keys;

        private readonly ImmutableDictionary<Symbol, OptionPosition> _positions;
        private readonly ImmutableDictionary<OptionRight, ImmutableHashSet<Symbol>> _rights;
        private readonly ImmutableSortedDictionary<decimal,  ImmutableHashSet<Symbol>> _strikes;
        private readonly ImmutableSortedDictionary<DateTime, ImmutableHashSet<Symbol>> _expirations;

        /// <summary>
        /// Initializes a new instance of the <see cref="OptionPositionCollection"/> class
        /// </summary>
        /// <param name="positions">All positions</param>
        /// <param name="rights">Index of position symbols by option right</param>
        /// <param name="strikes">Index of position symbols by strike price</param>
        /// <param name="expirations">Index of position symbols by expiration</param>
        public OptionPositionCollection(
            ImmutableDictionary<Symbol, OptionPosition> positions,
            ImmutableDictionary<OptionRight, ImmutableHashSet<Symbol>> rights,
            ImmutableSortedDictionary<decimal, ImmutableHashSet<Symbol>> strikes,
            ImmutableSortedDictionary<DateTime, ImmutableHashSet<Symbol>> expirations
            )
        {
            _rights = rights;
            _strikes = strikes;
            _positions = positions;
            _expirations = expirations;

            if (_rights.Count != 2)
            {
                // ensure we always have both rights indexed, even if empty
                ImmutableHashSet<Symbol> value;
                if (!_rights.TryGetValue(OptionRight.Call, out value))
                {
                    _rights = _rights.SetItem(OptionRight.Call, ImmutableHashSet<Symbol>.Empty);
                }
                if (!_rights.TryGetValue(OptionRight.Put, out value))
                {
                    _rights = _rights.SetItem(OptionRight.Put, ImmutableHashSet<Symbol>.Empty);
                }
            }

            if (!positions.IsEmpty)
            {
                // assumption here is that 'positions' includes the underlying equity position and
                // ONLY option contracts, so all symbols have the underlying equity symbol embedded
                // via the Underlying property, except of course, for the underlying itself.
                var underlying = positions.First().Key;
                if (underlying.HasUnderlying)
                {
                    underlying = underlying.Underlying;
                }

                // OptionPosition is struct, so no worry about null ref via .Quantity
                var underlyingQuantity = positions.GetValueOrDefault(underlying).Quantity;
                UnderlyingPosition = new OptionPosition(underlying, underlyingQuantity);
            }
#if DEBUG
            var errors = Validate().ToList();
            if (errors.Count > 0)
            {
                throw new ArgumentException("OptionPositionCollection validation failed: "
                    + Environment.NewLine + string.Join(Environment.NewLine, errors)
                );
            }
#endif
        }

        /// <summary>
        /// Determines if a position is held in the specified <paramref name="symbol"/>
        /// </summary>
        public bool HasPosition(Symbol symbol)
        {
            OptionPosition position;
            return TryGetPosition(symbol, out position) && position.Quantity != 0;
        }

        /// <summary>
        /// Retrieves the <see cref="OptionPosition"/> for the specified <paramref name="symbol"/>
        /// if one exists in this collection.
        /// </summary>
        public bool TryGetPosition(Symbol symbol, out OptionPosition position)
        {
            return _positions.TryGetValue(symbol, out position);
        }

        /// <summary>
        /// Gets the underlying security's position
        /// </summary>
        /// <returns></returns>
        public OptionPosition GetUnderlyingPosition()
            => LinqExtensions.GetValueOrDefault(_positions, Underlying, new OptionPosition(Underlying, 0));

        /// <summary>
        /// Creates a new <see cref="OptionPositionCollection"/> from the specified <paramref name="holdings"/>,
        /// filtering based on the <paramref name="underlying"/>
        /// </summary>
        public static OptionPositionCollection Create(Symbol underlying, decimal contractMultiplier, IEnumerable<SecurityHolding> holdings)
        {
            var positions = Empty;
            foreach (var holding in holdings)
            {
                var symbol = holding.Symbol;
                if (!symbol.HasUnderlying)
                {
                    if (symbol == underlying)
                    {
                        var underlyingLots = (int) (holding.Quantity / contractMultiplier);
                        positions = positions.Add(new OptionPosition(symbol, underlyingLots));
                    }

                    continue;
                }

                if (symbol.Underlying != underlying)
                {
                    continue;
                }

                var position = new OptionPosition(symbol, (int) holding.Quantity);
                positions = positions.Add(position);
            }

            return positions;
        }

        /// <summary>
        /// Creates a new collection that is the result of adding the specified <paramref name="position"/> to this collection.
        /// </summary>
        public OptionPositionCollection Add(OptionPosition position)
        {
            OptionPosition existing;
            var symbol = position.Symbol;
            if (_positions.TryGetValue(symbol, out existing) || !symbol.HasUnderlying)
            {
                position += existing;

                if (position.Exists)
                {
                    // if the position already exists then it's already indexed
                    return new OptionPositionCollection(
                        _positions.SetItem(symbol, position),
                        _rights,
                        _strikes,
                        _expirations
                    );
                }

                // if the position's quantity went to zero, remove it entirely from the collection when
                // removing, be sure to remove strike/expiration indexes. we purposefully keep the rights
                // index populated, even with a zero count entry because it's bounded to 2 items (put/call)
                var strikesValue = _strikes[position.Strike].Remove(symbol);
                var expirationsValue = _expirations[position.Expiration].Remove(symbol);

                return new OptionPositionCollection(
                    _positions.Remove(symbol),
                    _rights.SetItem(position.Right, _rights[position.Right].Remove(symbol)),
                    strikesValue.Count > 0
                        ? _strikes.SetItem(position.Strike, strikesValue)
                        : _strikes.Remove(position.Strike),
                    expirationsValue.Count > 0
                        ? _expirations.SetItem(position.Expiration, expirationsValue)
                        : _expirations.Remove(position.Expiration)
                );
            }

            // add symbol to indexes
            return new OptionPositionCollection(
                _positions.SetItem(symbol, position),
                _rights.Add(position.Right, symbol),
                _strikes.Add(position.Strike, symbol),
                _expirations.Add(position.Expiration, symbol)
            );
        }

        /// <summary>
        /// Creates a new collection that is the result of adding the specified <paramref name="positions"/> to this collection.
        /// </summary>
        public OptionPositionCollection AddRange(IEnumerable<OptionPosition> positions)
        {
            var pos = _positions;
            var rights = _rights;
            var strikes = _strikes;
            var expirations = _expirations;
            foreach (var position in positions)
            {
                var symbol = position.Symbol;
                pos = pos.Add(symbol, position);

                // index all option positions
                if (symbol.HasUnderlying)
                {
                    rights = rights.Add(position.Right, symbol);
                    strikes = strikes.Add(position.Strike, symbol);
                    expirations = expirations.Add(position.Expiration, symbol);
                }
            }

            return new OptionPositionCollection(pos, rights, strikes, expirations);
        }

        /// <summary>
        /// Slices this collection, returning a new collection containing only
        /// positions with the specified <paramref name="right"/>
        /// </summary>
        public OptionPositionCollection Slice(OptionRight right, bool includeUnderlying = true)
        {
            var rights = _rights.Remove(right.Invert());

            var positions = ImmutableDictionary<Symbol, OptionPosition>.Empty;
            if (includeUnderlying && HasUnderlying)
            {
                positions = positions.Add(Underlying, UnderlyingPosition);
            }

            var strikes = ImmutableSortedDictionary<decimal, ImmutableHashSet<Symbol>>.Empty;
            var expirations = ImmutableSortedDictionary<DateTime, ImmutableHashSet<Symbol>>.Empty;
            foreach (var symbol in rights.SelectMany(kvp => kvp.Value))
            {
                var position = _positions[symbol];
                positions = positions.Add(symbol, position);
                strikes = strikes.Add(position.Strike, symbol);
                expirations = expirations.Add(position.Expiration, symbol);
            }

            return new OptionPositionCollection(positions, rights, strikes, expirations);
        }

        /// <summary>
        /// Slices this collection, returning a new collection containing only
        /// positions matching the specified <paramref name="comparison"/> and <paramref name="strike"/>
        /// </summary>
        public OptionPositionCollection Slice(BinaryComparison comparison, decimal strike, bool includeUnderlying = true)
        {
            var strikes = comparison.Filter(_strikes, strike);
            if (strikes.IsEmpty)
            {
                return includeUnderlying && HasUnderlying ? Empty.Add(UnderlyingPosition) : Empty;
            }

            var positions = ImmutableDictionary<Symbol, OptionPosition>.Empty;
            if (includeUnderlying)
            {
                OptionPosition underlyingPosition;
                if (_positions.TryGetValue(Underlying, out underlyingPosition))
                {
                    positions = positions.Add(Underlying, underlyingPosition);
                }
            }

            var rights = ImmutableDictionary<OptionRight, ImmutableHashSet<Symbol>>.Empty;
            var expirations = ImmutableSortedDictionary<DateTime, ImmutableHashSet<Symbol>>.Empty;
            foreach (var symbol in strikes.SelectMany(kvp => kvp.Value))
            {
                var position = _positions[symbol];
                positions = positions.Add(symbol, position);
                rights = rights.Add(symbol.ID.OptionRight, symbol);
                expirations = expirations.Add(symbol.ID.Date, symbol);
            }

            return new OptionPositionCollection(positions, rights, strikes, expirations);
        }

        /// <summary>
        /// Slices this collection, returning a new collection containing only
        /// positions matching the specified <paramref name="comparison"/> and <paramref name="expiration"/>
        /// </summary>
        public OptionPositionCollection Slice(BinaryComparison comparison, DateTime expiration, bool includeUnderlying = true)
        {
            var expirations = comparison.Filter(_expirations, expiration);
            if (expirations.IsEmpty)
            {
                return includeUnderlying && HasUnderlying ? Empty.Add(UnderlyingPosition) : Empty;
            }

            var positions = ImmutableDictionary<Symbol, OptionPosition>.Empty;
            if (includeUnderlying)
            {
                OptionPosition underlyingPosition;
                if (_positions.TryGetValue(Underlying, out underlyingPosition))
                {
                    positions = positions.Add(Underlying, underlyingPosition);
                }
            }

            var rights = ImmutableDictionary<OptionRight, ImmutableHashSet<Symbol>>.Empty;
            var strikes = ImmutableSortedDictionary<decimal, ImmutableHashSet<Symbol>>.Empty;
            foreach (var symbol in expirations.SelectMany(kvp => kvp.Value))
            {
                var position = _positions[symbol];
                positions = positions.Add(symbol, position);
                rights = rights.Add(symbol.ID.OptionRight, symbol);
                strikes = strikes.Add(symbol.ID.StrikePrice, symbol);
            }

            return new OptionPositionCollection(positions, rights, strikes, expirations);
        }

        public IEnumerable<OptionPosition> ForSymbols(IEnumerable<Symbol> symbols)
        {
            foreach (var symbol in symbols)
            {
                OptionPosition position;
                if (_positions.TryGetValue(symbol, out position))
                {
                    yield return position;
                }
            }
        }

        public IEnumerable<OptionPosition> ForStrike(decimal strike)
        {
            ImmutableHashSet<Symbol> symbols;
            return _strikes.TryGetValue(strike, out symbols)
                ? ForSymbols(symbols)
                : Enumerable.Empty<OptionPosition>();
        }

        public IEnumerable<OptionPosition> ForExpiration(DateTime expiration)
        {
            ImmutableHashSet<Symbol> symbols;
            return _expirations.TryGetValue(expiration, out symbols)
                ? ForSymbols(symbols)
                : Enumerable.Empty<OptionPosition>();
        }

        /// <summary>
        /// Deducts the positions contained within the specified <paramref name="match"/>
        /// </summary>
        public OptionPositionCollection Accept(OptionStrategyDefinitionMatch match)
        {
            var positions = this;
            foreach (var leg in match.Legs)
            {
                positions -= leg.Position;
            }

            return positions;
        }

        /// <summary>Returns an enumerator that iterates through the collection.</summary>
        /// <returns>An enumerator that can be used to iterate through the collection.</returns>
        public IEnumerator<OptionPosition> GetEnumerator()
        {
            return _positions.Select(kvp => kvp.Value).GetEnumerator();
        }

        /// <summary>
        /// Validates this collection returning an enumerable of validation errors.
        /// This should only be invoked via tests and is automatically invoked via
        /// the constructor in DEBUG builds.
        /// </summary>
        internal IEnumerable<string> Validate()
        {
            foreach (var kvp in _positions)
            {
                var position = kvp.Value;
                var symbol = position.Symbol;
                if (position.Quantity == 0)
                {
                    yield return $"{position}: Quantity == 0";
                }

                if (!symbol.HasUnderlying)
                {
                    continue;
                }

                ImmutableHashSet<Symbol> strikes;
                if (!_strikes.TryGetValue(position.Strike, out strikes) || !strikes.Contains(symbol))
                {
                    yield return $"{position}: Not indexed by strike price";
                }

                ImmutableHashSet<Symbol> expirations;
                if (!_expirations.TryGetValue(position.Expiration, out expirations) || !expirations.Contains(symbol))
                {
                    yield return $"{position}: Not indexed by expiration date";
                }
            }
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public static OptionPositionCollection operator+(OptionPositionCollection positions, OptionPosition position)
        {
            return positions.Add(position);
        }

        public static OptionPositionCollection operator-(OptionPositionCollection positions, OptionPosition position)
        {
            return positions.Add(position.Negate());
        }
    }
}
