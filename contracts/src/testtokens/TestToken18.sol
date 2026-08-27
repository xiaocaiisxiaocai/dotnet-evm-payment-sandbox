// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

import {ERC20} from "@openzeppelin/contracts/token/ERC20/ERC20.sol";

/// @title TestToken18
/// @notice Plain 18-decimal ERC-20 used to catch hard-coded six-decimal logic.
/// @dev TEST ONLY: minting is intentionally unrestricted. This token also omits
///      ERC-2612 so tests can prove payWithPermit rejects non-permit tokens.
contract TestToken18 is ERC20 {
    constructor() ERC20("Test Token 18", "TT18") {}

    /// @notice Creates test balance without access control.
    /// @dev ERC20's default decimals value is 18; it is left unmodified here so
    ///      tests exercise the standard OpenZeppelin behavior.
    function mint(address to, uint256 amount) external {
        _mint(to, amount);
    }
}
