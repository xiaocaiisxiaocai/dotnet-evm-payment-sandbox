// SPDX-License-Identifier: MIT
pragma solidity 0.8.36;

/// @notice Test fixture that burns one percent of every transferFrom amount.
/// @dev TEST ONLY: this deliberately small ERC-20-like contract exists to show
///      that a successful token call does not prove how much the recipient got.
///      It implements only the behavior needed by PaymentRouter tests.
contract FeeOnTransferToken {
    uint256 internal constant FEE_DIVISOR = 100;

    mapping(address account => uint256 amount) public balanceOf;
    mapping(address owner => mapping(address spender => uint256 amount)) public allowance;

    event Transfer(address indexed from, address indexed to, uint256 amount);
    event Approval(address indexed owner, address indexed spender, uint256 amount);

    error InsufficientBalance(address account, uint256 balance, uint256 needed);
    error InsufficientAllowance(address spender, uint256 allowance, uint256 needed);

    function mint(address to, uint256 amount) external {
        balanceOf[to] += amount;
        emit Transfer(address(0), to, amount);
    }

    function approve(address spender, uint256 amount) external returns (bool) {
        allowance[msg.sender][spender] = amount;
        emit Approval(msg.sender, spender, amount);
        return true;
    }

    function transferFrom(address from, address to, uint256 amount) external returns (bool) {
        uint256 currentAllowance = allowance[from][msg.sender];
        if (currentAllowance < amount) {
            revert InsufficientAllowance(msg.sender, currentAllowance, amount);
        }

        uint256 currentBalance = balanceOf[from];
        if (currentBalance < amount) revert InsufficientBalance(from, currentBalance, amount);

        // Infinite approval follows the common ERC-20 convention. The test uses
        // an exact approval so it can also prove how much allowance was spent.
        if (currentAllowance != type(uint256).max) {
            allowance[from][msg.sender] = currentAllowance - amount;
            emit Approval(from, msg.sender, currentAllowance - amount);
        }

        uint256 fee = amount / FEE_DIVISOR;
        uint256 received = amount - fee;

        balanceOf[from] = currentBalance - amount;
        balanceOf[to] += received;

        emit Transfer(from, to, received);
        if (fee != 0) emit Transfer(from, address(0), fee);
        return true;
    }
}
